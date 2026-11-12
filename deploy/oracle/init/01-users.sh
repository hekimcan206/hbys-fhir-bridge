#!/bin/bash
# Runs once, on the first start of the gvenzl/oracle-free container, as the oracle OS user.
# HBYS        owns the synthetic hospital schema and its change journal (stands in for the HBYS vendor schema).
# HBYS_BRIDGE is the bridge's login: read-only on the clinical tables, delete on the journal (purge) and its own
#             checkpoint/parking tables. It cannot change a single clinical row.
set -euo pipefail

: "${HBYS_PASSWORD:?HBYS_PASSWORD must be set}"
: "${BRIDGE_PASSWORD:?BRIDGE_PASSWORD must be set}"

sqlplus -s / as sysdba <<SQL
WHENEVER SQLERROR EXIT SQL.SQLCODE
ALTER SESSION SET CONTAINER = FREEPDB1;

CREATE USER hbys IDENTIFIED BY "${HBYS_PASSWORD}" DEFAULT TABLESPACE users QUOTA 1G ON users;
GRANT CREATE SESSION, CREATE TABLE, CREATE TRIGGER, CREATE SEQUENCE, CREATE PROCEDURE TO hbys;

CREATE USER hbys_bridge IDENTIFIED BY "${BRIDGE_PASSWORD}" DEFAULT TABLESPACE users QUOTA 256M ON users;
GRANT CREATE SESSION, CREATE TABLE TO hbys_bridge;
-- The bridge reads the database's current SCN to cut a consistent change window (see docs/adr/0002).
GRANT EXECUTE ON SYS.DBMS_FLASHBACK TO hbys_bridge;
EXIT
SQL
