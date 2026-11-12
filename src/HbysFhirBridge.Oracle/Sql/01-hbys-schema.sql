-- Synthetic HBYS (Hastane Bilgi Yonetim Sistemi = hospital information system) schema, owned by HBYS.
-- Table and column names follow the Turkish conventions such systems use; the README explains each one.
-- Every statement ends with a line holding a single "/" so the installer can split PL/SQL safely.

-- HASTA = patient. DOSYA_NO = hospital file number (MRN). TC_KIMLIK_NO = national id (synthetic, checksum-valid).
-- CINSIYET: E = erkek (male), K = kadin (female), B = bilinmiyor (unknown). AKTIF: E = evet (yes), H = hayir (no).
CREATE TABLE hasta (
    hasta_id          NUMBER(12)    CONSTRAINT pk_hasta PRIMARY KEY,
    dosya_no          VARCHAR2(20)  NOT NULL CONSTRAINT uq_hasta_dosya UNIQUE,
    tc_kimlik_no      CHAR(11),
    ad                VARCHAR2(60)  NOT NULL,
    soyad             VARCHAR2(60)  NOT NULL,
    cinsiyet          CHAR(1)       NOT NULL CONSTRAINT ck_hasta_cinsiyet CHECK (cinsiyet IN ('E', 'K', 'B')),
    dogum_tarihi      DATE,
    telefon           VARCHAR2(20),
    il                VARCHAR2(40),
    ilce              VARCHAR2(40),
    olum_tarihi       DATE,
    aktif             CHAR(1)       DEFAULT 'E' NOT NULL CONSTRAINT ck_hasta_aktif CHECK (aktif IN ('E', 'H')),
    guncelleme_zamani TIMESTAMP(6)  DEFAULT SYSTIMESTAMP NOT NULL
)
/
-- BASVURU = visit / admission. BASVURU_TIPI: A = ayaktan (outpatient), Y = yatan (inpatient), C = acil (emergency).
-- DURUM: PLANLANDI planned, DEVAM in progress, TAMAMLANDI finished, IPTAL cancelled, HATALI entered in error.
CREATE TABLE basvuru (
    basvuru_id        NUMBER(12)    CONSTRAINT pk_basvuru PRIMARY KEY,
    hasta_id          NUMBER(12)    NOT NULL CONSTRAINT fk_basvuru_hasta REFERENCES hasta,
    basvuru_tipi      CHAR(1)       NOT NULL CONSTRAINT ck_basvuru_tipi CHECK (basvuru_tipi IN ('A', 'Y', 'C')),
    durum             VARCHAR2(12)  NOT NULL
        CONSTRAINT ck_basvuru_durum CHECK (durum IN ('PLANLANDI', 'DEVAM', 'TAMAMLANDI', 'IPTAL', 'HATALI')),
    baslangic         TIMESTAMP(0)  NOT NULL,
    bitis             TIMESTAMP(0),
    brans_kodu        VARCHAR2(10),
    guncelleme_zamani TIMESTAMP(6)  DEFAULT SYSTIMESTAMP NOT NULL
)
/
CREATE INDEX ix_basvuru_hasta ON basvuru (hasta_id)
/
-- LAB_SONUC = laboratory result. TETKIK_KODU = local test code. SONUC_DEGER numeric value, SONUC_METIN text value.
-- REF_ALT / REF_UST = reference range low / high. DURUM: ON preliminary, KESIN final, DUZELTILDI corrected,
-- IPTAL cancelled, HATALI entered in error.
CREATE TABLE lab_sonuc (
    sonuc_id          NUMBER(12)    CONSTRAINT pk_lab_sonuc PRIMARY KEY,
    basvuru_id        NUMBER(12)    NOT NULL CONSTRAINT fk_sonuc_basvuru REFERENCES basvuru,
    hasta_id          NUMBER(12)    NOT NULL CONSTRAINT fk_sonuc_hasta REFERENCES hasta,
    tetkik_kodu       VARCHAR2(20)  NOT NULL,
    sonuc_deger       NUMBER(12, 4),
    sonuc_metin       VARCHAR2(200),
    birim             VARCHAR2(20),
    ref_alt           NUMBER(12, 4),
    ref_ust           NUMBER(12, 4),
    durum             VARCHAR2(12)  NOT NULL
        CONSTRAINT ck_sonuc_durum CHECK (durum IN ('ON', 'KESIN', 'DUZELTILDI', 'IPTAL', 'HATALI')),
    sonuc_zamani      TIMESTAMP(0)  NOT NULL,
    guncelleme_zamani TIMESTAMP(6)  DEFAULT SYSTIMESTAMP NOT NULL
)
/
CREATE INDEX ix_sonuc_basvuru ON lab_sonuc (basvuru_id)
/
CREATE INDEX ix_sonuc_hasta ON lab_sonuc (hasta_id)
/
-- Indexes the naive "last updated" extraction strategy needs; kept so the comparison is fair.
CREATE INDEX ix_hasta_guncelleme ON hasta (guncelleme_zamani)
/
CREATE INDEX ix_basvuru_guncelleme ON basvuru (guncelleme_zamani)
/
CREATE INDEX ix_sonuc_guncelleme ON lab_sonuc (guncelleme_zamani)
/
-- DEGISIKLIK_GUNLUGU = change journal. One row per changed source row, written by the triggers below in the same
-- transaction as the change, so a change and its journal entry commit or roll back together. The bridge reads the
-- journal and the row states AS OF one SCN and deletes exactly the entries it delivered; an entry committed later
-- is simply still there next time. No watermark, so nothing can be skipped (docs/adr/0002).
-- ISLEM: I insert, U update, D delete.
CREATE TABLE degisiklik_gunlugu (
    gunluk_id    NUMBER        GENERATED ALWAYS AS IDENTITY CONSTRAINT pk_gunluk PRIMARY KEY,
    tablo_adi    VARCHAR2(30)  NOT NULL,
    kayit_id     NUMBER(12)    NOT NULL,
    islem        CHAR(1)       NOT NULL CONSTRAINT ck_gunluk_islem CHECK (islem IN ('I', 'U', 'D')),
    islem_zamani TIMESTAMP(6)  DEFAULT SYSTIMESTAMP NOT NULL
)
/
CREATE OR REPLACE TRIGGER trg_hasta_cdc
AFTER INSERT OR UPDATE OR DELETE ON hasta
FOR EACH ROW
DECLARE
    -- INSERTING/UPDATING are PL/SQL predicates and cannot appear inside the SQL statement itself.
    v_islem CHAR(1) := CASE WHEN INSERTING THEN 'I' WHEN UPDATING THEN 'U' ELSE 'D' END;
BEGIN
    INSERT INTO degisiklik_gunlugu (tablo_adi, kayit_id, islem)
    VALUES ('HASTA', COALESCE(:NEW.hasta_id, :OLD.hasta_id), v_islem);
END;
/
CREATE OR REPLACE TRIGGER trg_basvuru_cdc
AFTER INSERT OR UPDATE OR DELETE ON basvuru
FOR EACH ROW
DECLARE
    -- INSERTING/UPDATING are PL/SQL predicates and cannot appear inside the SQL statement itself.
    v_islem CHAR(1) := CASE WHEN INSERTING THEN 'I' WHEN UPDATING THEN 'U' ELSE 'D' END;
BEGIN
    INSERT INTO degisiklik_gunlugu (tablo_adi, kayit_id, islem)
    VALUES ('BASVURU', COALESCE(:NEW.basvuru_id, :OLD.basvuru_id), v_islem);
END;
/
CREATE OR REPLACE TRIGGER trg_lab_sonuc_cdc
AFTER INSERT OR UPDATE OR DELETE ON lab_sonuc
FOR EACH ROW
DECLARE
    -- INSERTING/UPDATING are PL/SQL predicates and cannot appear inside the SQL statement itself.
    v_islem CHAR(1) := CASE WHEN INSERTING THEN 'I' WHEN UPDATING THEN 'U' ELSE 'D' END;
BEGIN
    INSERT INTO degisiklik_gunlugu (tablo_adi, kayit_id, islem)
    VALUES ('LAB_SONUC', COALESCE(:NEW.sonuc_id, :OLD.sonuc_id), v_islem);
END;
/
-- "Last updated" stamps, the way many HBYS integrations detect changes. The timestamp is taken when the statement
-- runs, not when the transaction commits; that gap is where the naive strategy loses rows.
CREATE OR REPLACE TRIGGER trg_hasta_stamp
BEFORE UPDATE ON hasta
FOR EACH ROW
BEGIN
    :NEW.guncelleme_zamani := SYSTIMESTAMP;
END;
/
CREATE OR REPLACE TRIGGER trg_basvuru_stamp
BEFORE UPDATE ON basvuru
FOR EACH ROW
BEGIN
    :NEW.guncelleme_zamani := SYSTIMESTAMP;
END;
/
CREATE OR REPLACE TRIGGER trg_lab_sonuc_stamp
BEFORE UPDATE ON lab_sonuc
FOR EACH ROW
BEGIN
    :NEW.guncelleme_zamani := SYSTIMESTAMP;
END;
/
-- Ids for rows created after the initial load (the live workload). The synthetic seed uses explicit ids below
-- 10,000,000, so the two never collide.
CREATE SEQUENCE seq_basvuru START WITH 10000000
/
CREATE SEQUENCE seq_lab_sonuc START WITH 10000000
/
