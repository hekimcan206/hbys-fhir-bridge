using System.Globalization;
using HbysFhirBridge.Source;
using Hl7.Fhir.Model;

namespace HbysFhirBridge.Mapping;

/// <summary>Why a row could not be turned into a resource at all (as opposed to producing an invalid one).</summary>
public static class MappingFailureReasons
{
    public const string UnmappedCode = "unmapped-code";
    public const string UnknownUnit = "unknown-unit";
    public const string InvalidSource = "invalid-source";
}

/// <summary>The outcome of mapping one source row: a resource, or a reason it could not be built.</summary>
public sealed record MappingResult(SourceKey Key, Resource? Resource, string? FailureReason, string? FailureDetail)
{
    public bool Succeeded => Resource is not null;

    public static MappingResult Ok(SourceKey key, Resource resource) => new(key, resource, null, null);

    public static MappingResult Fail(SourceKey key, string reason, string detail) => new(key, null, reason, detail);
}

/// <summary>
/// Pure, deterministic HBYS row to FHIR R4 mapping. No I/O and no clock: the same row always produces the same
/// resource, which is what makes the golden tests and idempotent re-sends possible.
/// </summary>
public sealed class FhirMapper(LabCatalog catalog, MappingOptions options)
{
    public FhirMapper()
        : this(LabCatalog.Default, new MappingOptions())
    {
    }

    public MappingResult Map(SourceKey key, object row) => row switch
    {
        HastaRow h => MapPatient(h),
        BasvuruRow b => MapEncounter(b),
        LabSonucRow l => MapObservation(l),
        _ => throw new ArgumentException($"Unsupported row type {row.GetType().Name} for {key}", nameof(row)),
    };

    public MappingResult MapPatient(HastaRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var key = new SourceKey(SourceTable.Hasta, row.HastaId);

        var patient = new Patient
        {
            Id = ResourceIds.For(key),
            Meta = new Meta { Profile = [Systems.PatientProfile] },
            Active = row.Aktif,
            Gender = row.Cinsiyet switch
            {
                'E' => AdministrativeGender.Male,
                'K' => AdministrativeGender.Female,
                _ => AdministrativeGender.Unknown,
            },
        };

        patient.Identifier.Add(new Identifier(Systems.DosyaNo, row.DosyaNo.Trim())
        {
            Use = Identifier.IdentifierUse.Usual,
            Type = new CodeableConcept(Systems.V2IdentifierType, "MR", "Medical record number"),
        });

        if (options.IncludeNationalId && !string.IsNullOrWhiteSpace(row.TcKimlikNo))
        {
            patient.Identifier.Add(new Identifier(Systems.TcKimlikNo, row.TcKimlikNo.Trim())
            {
                Use = Identifier.IdentifierUse.Official,
                Type = new CodeableConcept(Systems.V2IdentifierType, "NI", "National unique individual identifier"),
            });
        }

        patient.Name.Add(new HumanName
        {
            Use = HumanName.NameUse.Official,
            Family = row.Soyad.Trim(),
            Given = [row.Ad.Trim()],
        });

        if (row.DogumTarihi is { } birth)
        {
            patient.BirthDate = birth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (row.OlumTarihi is { } death)
        {
            patient.Deceased = new FhirDateTime(death.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(row.Telefon))
        {
            patient.Telecom.Add(new ContactPoint(ContactPoint.ContactPointSystem.Phone, ContactPoint.ContactPointUse.Mobile, row.Telefon.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(row.Il) || !string.IsNullOrWhiteSpace(row.Ilce))
        {
            patient.Address.Add(new Address
            {
                Use = Address.AddressUse.Home,
                City = row.Il?.Trim(),
                District = row.Ilce?.Trim(),
                Country = "TR",
            });
        }

        return MappingResult.Ok(key, patient);
    }

    public MappingResult MapEncounter(BasvuruRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var key = new SourceKey(SourceTable.Basvuru, row.BasvuruId);

        Encounter.EncounterStatus? status = row.Durum switch
        {
            "PLANLANDI" => Encounter.EncounterStatus.Planned,
            "DEVAM" => Encounter.EncounterStatus.InProgress,
            "TAMAMLANDI" => Encounter.EncounterStatus.Finished,
            "IPTAL" => Encounter.EncounterStatus.Cancelled,
            "HATALI" => Encounter.EncounterStatus.EnteredInError,
            _ => null,
        };
        if (status is null)
        {
            return MappingResult.Fail(key, MappingFailureReasons.InvalidSource, $"Unknown BASVURU.DURUM '{row.Durum}'");
        }

        Coding? encounterClass = row.BasvuruTipi switch
        {
            'A' => new Coding(Systems.V3ActCode, "AMB", "ambulatory"),
            'Y' => new Coding(Systems.V3ActCode, "IMP", "inpatient encounter"),
            'C' => new Coding(Systems.V3ActCode, "EMER", "emergency"),
            _ => null,
        };
        if (encounterClass is null)
        {
            return MappingResult.Fail(key, MappingFailureReasons.InvalidSource, $"Unknown BASVURU.BASVURU_TIPI '{row.BasvuruTipi}'");
        }

        var encounter = new Encounter
        {
            Id = ResourceIds.For(key),
            Meta = new Meta { Profile = [Systems.EncounterProfile] },
            Status = status,
            Class = encounterClass,
            Subject = new ResourceReference(ResourceIds.Reference(SourceTable.Hasta, row.HastaId)),
            Period = new Period
            {
                StartElement = ToFhirDateTime(row.Baslangic),
                EndElement = row.Bitis is { } end ? ToFhirDateTime(end) : null,
            },
        };
        encounter.Identifier.Add(new Identifier(Systems.BasvuruNo, row.BasvuruId.ToString(CultureInfo.InvariantCulture)));

        if (!string.IsNullOrWhiteSpace(row.BransKodu))
        {
            encounter.ServiceType = new CodeableConcept(Systems.Department, row.BransKodu.Trim());
        }

        return MappingResult.Ok(key, encounter);
    }

    public MappingResult MapObservation(LabSonucRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var key = new SourceKey(SourceTable.LabSonuc, row.SonucId);

        if (!catalog.TryGet(row.TetkikKodu, out var test))
        {
            return MappingResult.Fail(key, MappingFailureReasons.UnmappedCode,
                $"TETKIK_KODU '{row.TetkikKodu}' has no LOINC mapping in the lab catalogue");
        }

        ObservationStatus? status = row.Durum switch
        {
            "ON" => ObservationStatus.Preliminary,
            "KESIN" => ObservationStatus.Final,
            "DUZELTILDI" => ObservationStatus.Corrected,
            "IPTAL" => ObservationStatus.Cancelled,
            "HATALI" => ObservationStatus.EnteredInError,
            _ => null,
        };
        if (status is null)
        {
            return MappingResult.Fail(key, MappingFailureReasons.InvalidSource, $"Unknown LAB_SONUC.DURUM '{row.Durum}'");
        }

        var observation = new Observation
        {
            Id = ResourceIds.For(key),
            Meta = new Meta { Profile = [Systems.LabObservationProfile] },
            Status = status,
            Category = [new CodeableConcept(Systems.ObservationCategory, "laboratory", "Laboratory")],
            Code = new CodeableConcept
            {
                Coding =
                [
                    new Coding(Systems.Loinc, test.Loinc, test.LoincDisplay),
                    new Coding(Systems.LocalTestCode, test.LocalCode, test.LocalName),
                ],
                Text = test.LocalName,
            },
            Subject = new ResourceReference(ResourceIds.Reference(SourceTable.Hasta, row.HastaId)),
            Encounter = new ResourceReference(ResourceIds.Reference(SourceTable.Basvuru, row.BasvuruId)),
            Effective = ToFhirDateTime(row.SonucZamani),
        };
        observation.Identifier.Add(new Identifier(Systems.SonucNo, row.SonucId.ToString(CultureInfo.InvariantCulture)));

        if (row.SonucDeger is { } value)
        {
            if (!LabCatalog.TryResolveUnit(test, row.Birim, out var ucum))
            {
                return MappingResult.Fail(key, MappingFailureReasons.UnknownUnit,
                    $"BIRIM '{row.Birim}' is not a known spelling of '{test.UcumUnit ?? "(no unit)"}' for {test.LocalCode}");
            }

            observation.Value = Quantity(value, ucum);
            if (row.RefAlt is not null || row.RefUst is not null)
            {
                observation.ReferenceRange.Add(new Observation.ReferenceRangeComponent
                {
                    Low = row.RefAlt is { } low ? Quantity(low, ucum) : null,
                    High = row.RefUst is { } high ? Quantity(high, ucum) : null,
                });
                if (Interpret(value, row.RefAlt, row.RefUst) is { } interpretation)
                {
                    observation.Interpretation.Add(interpretation);
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(row.SonucMetin))
        {
            observation.Value = new FhirString(row.SonucMetin.Trim());
        }
        else if (status is ObservationStatus.Final or ObservationStatus.Corrected)
        {
            // A released result with nothing in it is a source data problem; say so explicitly instead of
            // publishing an empty "final" result.
            observation.DataAbsentReason = new CodeableConcept(Systems.DataAbsentReason, "unknown", "Unknown");
        }

        return MappingResult.Ok(key, observation);
    }

    private static Quantity Quantity(decimal value, string ucum) => new()
    {
        // Oracle NUMBER(12,4) comes back with trailing zeros (5.6000); normalise so equal values serialise equally.
        Value = value / 1.0000000000000000000000000000m,
        Unit = ucum,
        System = Systems.Ucum,
        Code = ucum,
    };

    private static CodeableConcept? Interpret(decimal value, decimal? low, decimal? high)
    {
        if (low is { } l && value < l)
        {
            return new CodeableConcept(Systems.ObservationInterpretation, "L", "Low");
        }

        if (high is { } h && value > h)
        {
            return new CodeableConcept(Systems.ObservationInterpretation, "H", "High");
        }

        return new CodeableConcept(Systems.ObservationInterpretation, "N", "Normal");
    }

    private FhirDateTime ToFhirDateTime(DateTime local) =>
        new(new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), options.SourceUtcOffset));
}
