namespace HbysFhirBridge.Mapping;

/// <summary>
/// Identifier and code system URIs. The <c>hbys.example.org</c> ones are placeholders for the hospital's own
/// namespace (README, "Identifier systems"); the others are the official HL7, LOINC and UCUM systems.
/// </summary>
public static class Systems
{
    public const string Base = "https://hbys.example.org/fhir";

    public const string DosyaNo = Base + "/sid/dosya-no";
    public const string BasvuruNo = Base + "/sid/basvuru-no";
    public const string SonucNo = Base + "/sid/sonuc-no";

    /// <summary>
    /// Turkish national identity number (TC Kimlik No). A deployment replaces this placeholder with the URI its
    /// national exchange mandates. Exported only when <see cref="MappingOptions.IncludeNationalId"/> is on.
    /// </summary>
    public const string TcKimlikNo = Base + "/sid/tc-kimlik-no";

    public const string LocalTestCode = Base + "/CodeSystem/tetkik-kodu";
    public const string Department = Base + "/CodeSystem/brans";

    public const string PatientProfile = Base + "/StructureDefinition/hbys-patient";
    public const string EncounterProfile = Base + "/StructureDefinition/hbys-encounter";
    public const string LabObservationProfile = Base + "/StructureDefinition/hbys-lab-observation";

    public const string Loinc = "http://loinc.org";
    public const string Ucum = "http://unitsofmeasure.org";
    public const string V2IdentifierType = "http://terminology.hl7.org/CodeSystem/v2-0203";
    public const string V3ActCode = "http://terminology.hl7.org/CodeSystem/v3-ActCode";
    public const string ObservationCategory = "http://terminology.hl7.org/CodeSystem/observation-category";
    public const string ObservationInterpretation = "http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation";
    public const string DataAbsentReason = "http://terminology.hl7.org/CodeSystem/data-absent-reason";
}
