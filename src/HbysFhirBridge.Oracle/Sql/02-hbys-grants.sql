-- Run as HBYS. {bridge} is replaced with the bridge login. Read-only on clinical data, with FLASHBACK so that a
-- batch reads the journal and every table AS OF one SCN; DELETE on the journal only, to consume delivered entries.
GRANT SELECT, FLASHBACK ON hasta TO {bridge}
/
GRANT SELECT, FLASHBACK ON basvuru TO {bridge}
/
GRANT SELECT, FLASHBACK ON lab_sonuc TO {bridge}
/
GRANT SELECT, FLASHBACK, DELETE ON degisiklik_gunlugu TO {bridge}
/
