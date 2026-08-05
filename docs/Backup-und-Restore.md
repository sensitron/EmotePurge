# Backup und Restore (Postgres)

Schließt Befund **S1-2** aus `Review-2026-07-29.md`: Aktuell existiert **kein** Backup
des EmotePurge-Datenbestands (Channels, Emotes, UsageStats, VoteSessions, User-Logins) —
alles liegt ausschließlich im lokalen Docker-Volume `postgres-data` auf dem VPS. Ein
`docker compose down -v`-Tippfehler, ein VPS-Crash oder ein defektes Volume bedeutet
heute unwiederbringlichen Totalverlust.

Dieses Dokument deckt zwei Dinge ab: die einmalige Einrichtung des nächtlichen
Backup-Jobs auf dem VPS, und — genauso wichtig — den tatsächlichen Restore. Ein
Backup, dessen Wiederherstellung nie geprobt wurde, ist kein Backup.

> **Stand 2026-08-04: eingerichtet und über dieses Dokument hinaus erweitert.**
> Die Kette läuft dreistufig: Cron 03:00 auf dem VPS → `/var/backups/emotepurge`
> (14 Tage) → täglicher **Pull** aufs unraid-NAS um 10:00 (rrsync-eingesperrter
> SSH-Key, `/mnt/user/Data/backup/emotepurge`, 30 Tage, bewusst ohne `--delete`)
> → rclone-Crypt-Upload nach OneDrive (60 Tage). Off-Site läuft also
> **pull-seitig vom NAS**, nicht über den `OFFSITE_ENABLED`-Pfad des Skripts —
> der bleibt im Skript deaktiviert. Betriebsdetails (Key-Namen, Skriptname
> `emotepurge-backup-pull`, Remote `secret_emotepurge`) im privaten Repo
> `sensitron/infra-docs`, `VPS-und-Homelab-2026-08-04.md`. SSH auf den VPS seit
> dem 2026-08-04 nur noch als sudo-User (root-Login gesperrt).

Das Skript selbst liegt unter [`scripts/backup-postgres.sh`](../scripts/backup-postgres.sh)
und ist so gebaut, dass ein fehlgeschlagener `pg_dump` niemals ein gültig aussehendes,
aber abgeschnittenes Archiv hinterlässt (dump zuerst in eine `.tmp`-Datei, Exit-Code +
Größe prüfen, erst dann atomar per `mv` umbenennen).

> **Werte in diesem Dokument, die aus dem Repo belegt sind:** Container-Name
> `emotepurge-postgres` (aus `docker-compose.prod.yml`; der Dev-Stack heißt seit
> S3-38 `emotepurge-dev-*`), Container-Namen `emotepurge-api`/`emotepurge-worker`,
> Datenbankname `emotepurge` (in beiden Compose-Dateien als `POSTGRES_DB`
> hartkodiert), DB-User-Default `emotepurge` (aus `.env.example`,
> `POSTGRES_USER`). **Werte, die NICHT aus dem Repo stammen** und vom Nutzer zu
> prüfen/anzupassen sind, sind unten explizit als Platzhalter markiert
> (Backup-Zielverzeichnis, VPS-Zugangsdaten, Off-Site-Ziel).

## 1. Einrichtung auf dem VPS

Alle Schritte laufen auf dem **Host** (VPS), nicht in einem Container — `docker`
muss auf dem PATH verfügbar sein und der ausführende User braucht Rechte, `docker
exec`/`docker inspect` gegen `emotepurge-postgres` auszuführen (i. d. R. root oder
ein User in der `docker`-Gruppe).

### 1.1 Skript ablegen

`<VPS-HOST>`/`<VPS-USER>` sind Platzhalter — durch die tatsächlichen SSH-Zugangsdaten
des VPS ersetzen.

```sh
# Vom lokalen Checkout aus:
scp scripts/backup-postgres.sh <VPS-USER>@<VPS-HOST>:/tmp/backup-postgres.sh
ssh <VPS-USER>@<VPS-HOST> 'sudo mv /tmp/backup-postgres.sh /usr/local/bin/backup-postgres.sh && sudo chmod 755 /usr/local/bin/backup-postgres.sh'
```

(Alternativ: Falls das Repo ohnehin auf dem VPS gecloned ist, genügt `sudo cp
scripts/backup-postgres.sh /usr/local/bin/backup-postgres.sh && sudo chmod 755
/usr/local/bin/backup-postgres.sh` direkt dort.)

### 1.2 Zielverzeichnis anlegen

Das Skript legt `$BACKUP_DIR` zwar selbst per `mkdir -p` an, empfohlen ist trotzdem
ein expliziter erster Schritt mit sinnvollen Rechten (nur root lesbar, da ein
Datenbank-Dump potenziell sensible Daten wie Twitch-Access-Tokens/User-Logins
enthält):

```sh
sudo mkdir -p /var/backups/emotepurge
sudo chmod 700 /var/backups/emotepurge
```

> `/var/backups/emotepurge` ist der Skript-Default (`BACKUP_DIR`), aber **nicht**
> aus dem Repo belegt — es ist der im Review (`Review-2026-07-29.md:146-149`)
> vorgeschlagene Pfad. Bei Bedarf per `BACKUP_DIR=...` überschreiben (s. Tabelle
> unten), z. B. falls auf dem VPS bereits eine andere Backup-Konvention existiert.

### 1.3 Einmal manuell testen

Vor dem Cron-Eintrag immer erst manuell laufen lassen, um Berechtigungsfehler o. Ä.
sofort zu sehen statt erst nachts:

```sh
sudo /usr/local/bin/backup-postgres.sh
echo "Exit-Code: $?"
ls -lh /var/backups/emotepurge
```

Erwartung: Exit-Code `0`, eine neue Datei
`emotepurge-<Datum>_<Uhrzeit>.sql.gz` mit plausibler Größe (nicht 0 Byte — bei
der aktuellen Datenmenge im niedrigen einstelligen MB-Bereich zu erwarten, wächst
mit der Zeit).

### 1.4 Cronjob einrichten (nächtlich)

Eine `/etc/cron.d/`-Datei anlegen (läuft dann unabhängig vom Crontab eines
einzelnen Users, mit explizitem User-Feld):

```sh
sudo tee /etc/cron.d/emotepurge-backup >/dev/null <<'EOF'
# EmotePurge: naechtliches Postgres-Backup, taeglich 03:00 Uhr Server-Zeit.
# Log-Ausgabe landet zusaetzlich zur Stdout/Stderr-Ausgabe von cron selbst
# (die je nach System-Konfiguration per Mail an root geht) in dieser Datei.
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
0 3 * * * root /usr/local/bin/backup-postgres.sh >> /var/log/emotepurge-backup.log 2>&1
EOF
sudo chmod 644 /etc/cron.d/emotepurge-backup
sudo chown root:root /etc/cron.d/emotepurge-backup
```

Zwei Details in diesem Block sind keine Kosmetik:

- **Die `PATH`-Zeile.** Dateien in `/etc/cron.d` erben den PATH aus `/etc/crontab`
  **nicht**; cron setzt für sie intern nur `/usr/bin:/bin`. Liegt `docker` auf
  diesem System woanders (z. B. `/usr/local/bin` oder als Snap), bricht das
  Skript nachts an seiner eigenen `command -v docker`-Prüfung ab, obwohl der
  manuelle Lauf aus 1.3 einwandfrei war. Mit `command -v docker` vorab prüfen.
- **Die Dateirechte.** cron **ignoriert** Dateien in `/etc/cron.d` stillschweigend,
  wenn sie gruppen- oder weltschreibbar sind oder nicht root gehören — ohne
  Fehlermeldung und ohne Logeintrag. Es passiert dann schlicht nie etwas.

Nicht bis 03:00 Uhr warten, um zu sehen ob es klappt: einmal `date` ausführen,
die Zeitangabe in der Datei auf zwei Minuten in der Zukunft setzen,
`tail -f /var/log/emotepurge-backup.log` mitlaufen lassen und danach auf
`0 3 * * *` zurückstellen. Erscheint gar nichts im Log, liegt es fast immer an
einem der beiden Punkte oben oder daran, dass der Dienst selbst nicht läuft
(`systemctl status cron`). Die dabei entstandene Testdatei kann liegen bleiben,
die Rotation räumt sie nach `RETENTION_DAYS` selbst weg.

Ebenfalls kurz prüfen: `timedatectl | grep -i 'time zone'`. „03:00 Uhr" ist
Server-Zeit — auf einem UTC-Server ist das je nach eigener Zeitzone mitten am
Tag, was für ein Backup unkritisch ist, aber man sollte wissen wann es läuft.

Falls vom Default abweichende Werte nötig sind (z. B. anderes `BACKUP_DIR`), die
Env-Variablen direkt in der Zeile setzen:

```
0 3 * * * root BACKUP_DIR=/mnt/backup-disk/emotepurge RETENTION_DAYS=30 /usr/local/bin/backup-postgres.sh >> /var/log/emotepurge-backup.log 2>&1
```

Ein einzelner Cronjob genügt — Backup und Rotation laufen im selben Skriptdurchlauf
(anders als der zweizeilige Vorschlag im Review, funktional identisch).

## 2. Restore

### 2.0 Risikofreie Restore-Probe

Der Weg, um regelmäßig zu prüfen, dass die Dumps überhaupt etwas taugen — ohne
die Live-Datenbank anzufassen und ohne Api/Worker zu stoppen. Der Dump wird in
eine Wegwerf-Datenbank auf demselben Container eingespielt und danach wieder
verworfen. Erstmals durchgeführt am 2026-07-30, erfolgreich.

```sh
# 1. Wegwerf-Datenbank anlegen:
docker exec emotepurge-postgres psql -U emotepurge -d postgres \
  -c 'CREATE DATABASE emotepurge_restoretest OWNER emotepurge;'

# 2. Dump einspielen -- ON_ERROR_STOP=1 ist der eigentliche Wert dieses Tests:
gunzip -c /var/backups/emotepurge/emotepurge-<DATUM>_<UHRZEIT>.sql.gz \
  | docker exec -i emotepurge-postgres psql -U emotepurge -d emotepurge_restoretest -v ON_ERROR_STOP=1 -q
echo "Exit-Code: $?"

# 3. Zeilenzahlen gegen die Live-Datenbank halten:
for db in emotepurge emotepurge_restoretest; do
  echo "== $db"
  docker exec -i emotepurge-postgres psql -U emotepurge -d "$db" -t <<'SQL'
SELECT 'Channels', count(*) FROM "Channels"
UNION ALL SELECT 'Emotes', count(*) FROM "Emotes"
UNION ALL SELECT 'UsageStats', count(*) FROM "UsageStats"
UNION ALL SELECT 'Users', count(*) FROM "Users"
UNION ALL SELECT 'VoteSessions', count(*) FROM "VoteSessions"
UNION ALL SELECT 'Votes', count(*) FROM "Votes"
UNION ALL SELECT 'Migrationen', count(*) FROM "__EFMigrationsHistory";
SQL
done

# 4. Aufraeumen:
docker exec emotepurge-postgres psql -U emotepurge -d postgres \
  -c 'DROP DATABASE emotepurge_restoretest;'
```

**Ohne `ON_ERROR_STOP=1` ist dieser Test wertlos:** `psql` rauscht sonst über
Fehler hinweg und beendet sich trotzdem mit Exit-Code 0 — der Restore *sähe*
erfolgreich aus, obwohl Tabellen fehlen. Erwartet wird Exit-Code 0 ohne Ausgabe.

Beim Zeilenvergleich sind Abweichungen normal und kein Fehlersignal:
`UsageStats`/`Votes` liegen in der Live-Datenbank höher (der Flush läuft alle 30
Sekunden weiter, seit der Dump gezogen wurde), und `Migrationen` liegt niedriger,
wenn seit dem Backup eine Migration hinzugekommen ist — genau der in 2.2 unten
beschriebene Fall, in dem nach einem echten Restore einmal
`dotnet ef database update` nachzuziehen ist. `Channels` und `Users` sollten
übereinstimmen.

Was diese Probe **nicht** abdeckt: den vollständigen Katastrophenablauf aus 2.1
bzw. 2.2 (Api/Worker stoppen, Live-Datenbank ersetzen). Dessen Mechanik ist im
Vergleich trivial; der Teil, der im Ernstfall still versagt — ein abgeschnittener
oder unlesbarer Dump — ist genau der hier geprüfte. Wer auch den kompletten
Ablauf üben will, tut das sinnvollerweise gegen den lokalen Docker-Stack, nicht
gegen Prod.

---

**Für die beiden folgenden, echten Restore-Wege gilt: `emotepurge-api` und
`emotepurge-worker` vorher stoppen.** Beide schreiben aktiv in die Datenbank
(Chat-Matching-Flush, 7TV-Resync, Join/Leave) — läuft einer der beiden während
des Restores weiter, sind Race Conditions mit teils widersprüchlichem Endzustand
möglich.

```sh
docker stop emotepurge-api emotepurge-worker
```

### 2.1 Restore in eine laufende, bereits befüllte Instanz

Der Normalfall: Postgres läuft, hat aber (versehentlich gelöschte/korrumpierte)
Daten, die durch den Stand aus dem Dump ersetzt werden sollen. Ein `pg_dump` im
hier verwendeten `--format=plain` enthält keine `DROP`-Anweisungen — ein direktes
Zurückspielen in eine nicht-leere Datenbank scheitert an `already exists`-Fehlern.
Die Datenbank muss daher erst geleert werden:

```sh
# Verbindung zur Wartungsdatenbank "postgres", NICHT zu "emotepurge" selbst --
# eine Datenbank kann sich nicht selbst droppen, waehrend die Verbindung offen ist.
docker exec -i emotepurge-postgres psql -U emotepurge -d postgres -c "DROP DATABASE emotepurge;"
docker exec -i emotepurge-postgres psql -U emotepurge -d postgres -c "CREATE DATABASE emotepurge OWNER emotepurge;"

# Dump einspielen (Dateiname anpassen):
gunzip -c /var/backups/emotepurge/emotepurge-2026-07-29_030000.sql.gz \
  | docker exec -i emotepurge-postgres psql -U emotepurge -d emotepurge

# Api/Worker wieder starten:
docker start emotepurge-api emotepurge-worker
```

Bei Erfolg gibt der `psql`-Restore-Lauf eine lange Folge von `CREATE
TABLE`/`COPY`/`ALTER TABLE`-Bestätigungen aus, ohne `ERROR:`-Zeilen dazwischen —
kurz durchscrollen und auf `ERROR` prüfen.

### 2.2 Sonderfall: Volume ist weg, Stack neu hochgezogen

Nach einem VPS-Totalverlust, einem versehentlichen `docker compose down -v` oder
einem neu aufgesetzten Host existiert das `postgres-data`-Volume überhaupt nicht
mehr. Wird der Compose-Stack neu gestartet, legt der offizielle Postgres-Container
automatisch ein frisches, leeres `postgres-data`-Volume sowie eine leere
`emotepurge`-Datenbank an (über die `POSTGRES_DB`/`POSTGRES_USER`-Env-Variablen in
`docker-compose.prod.yml`) — in diesem Fall **ohne** Schema, da die EF-Core-
Migrationen nur beim erstmaligen App-Start liefen.

```sh
# 1. Sicherstellen, dass Api/Worker NICHT laufen, waehrend die DB neu befuellt wird.
docker stop emotepurge-api emotepurge-worker

# 2. Postgres (neu) hochfahren -- Docker legt das fehlende Volume automatisch neu an.
#    Pfad zur Compose-Datei auf dem VPS ggf. anpassen (haengt davon ab, wo der
#    Portainer-Stack die Datei ablegt bzw. ob sie dort ueberhaupt als Datei existiert --
#    alternativ ueber die Portainer-UI neu deployen).
docker compose -f docker-compose.prod.yml up -d postgres

# Warten, bis der Healthcheck "healthy" meldet:
docker ps --filter name=emotepurge-postgres --format '{{.Status}}'

# 3. Dump einspielen. Ein pg_dump im Plain-Format enthaelt volles Schema + Daten
#    (inkl. der EF-Core-Migrationshistorie-Tabelle) -- die frisch angelegte, leere
#    "emotepurge"-Datenbank aus Schritt 2 muss NICHT vorher gedroppt/neu angelegt
#    werden, ein direktes Einspielen in die leere DB genuegt:
gunzip -c /var/backups/emotepurge/emotepurge-<DATUM>_<UHRZEIT>.sql.gz \
  | docker exec -i emotepurge-postgres psql -U emotepurge -d emotepurge

# 4. Restlichen Stack starten:
docker compose -f docker-compose.prod.yml up -d api worker
# oder, falls die Compose-Datei auf dem VPS nicht verfuegbar ist:
docker start emotepurge-api emotepurge-worker
```

Da der Dump das komplette Schema mitbringt, ist ein separater
`dotnet ef database update`-Lauf für diesen Restore-Pfad **nicht** nötig — nur
relevant, falls seit dem letzten Backup neue Migrationen im laufenden Betrieb
hinzugekommen sind, die im Dump noch fehlen (dann nach dem Restore einmal
`dotnet ef database update` wie in `CLAUDE.md`, Abschnitt "EF Core Migrationen",
beschrieben nachziehen).

## 3. Off-Site-Kopie — Backup ≠ Schutz vor VPS-Totalverlust

**Ein Dump, der auf demselben VPS liegt wie die Datenbank, die er sichert, schützt
nicht vor VPS-Totalverlust** (Hosting-Kündigung/-Ausfall, Festplattendefekt,
kompromittierter Host, versehentliches Löschen der ganzen Maschine). Er schützt
ausschließlich vor Datenfehlern *innerhalb* des laufenden Systems (Fehlbedienung,
Volume-Korruption, fehlgeschlagenes Upgrade).

Das Skript bringt dafür einen optionalen, standardmäßig **deaktivierten**
Off-Site-Schritt per [`rclone`](https://rclone.org/) mit (`OFFSITE_ENABLED=1` +
`OFFSITE_RCLONE_REMOTE=<remote>:<pfad>`). Konkreter, unverbindlicher Vorschlag aus
dem Review: ein kostenloses [Backblaze B2](https://www.backblaze.com/cloud-storage)-
Free-Tier-Bucket (bis 10 GB, für Textdumps dieser Größenordnung lange ausreichend).
Einrichtung (Platzhalter `<b2-key-id>`/`<b2-app-key>`/`<bucket>` durch echte Werte
ersetzen):

```sh
# Einmalig, auf dem VPS:
sudo apt-get install -y rclone   # oder: curl https://rclone.org/install.sh | sudo bash
rclone config   # Remote-Typ "Backblaze B2" waehlen, Key-ID/App-Key eintragen, Remote-Name z.B. "b2"

# Danach im Cronjob (s. 1.4) ergaenzen:
0 3 * * * root OFFSITE_ENABLED=1 OFFSITE_RCLONE_REMOTE=b2:<bucket>/emotepurge /usr/local/bin/backup-postgres.sh >> /var/log/emotepurge-backup.log 2>&1
```

Falls `rclone` fehlt oder `OFFSITE_RCLONE_REMOTE` nicht gesetzt ist, überspringt
das Skript diesen Schritt nur mit einer Log-Warnung — das lokale Backup schlägt
dadurch nie fehl, nur weil die Off-Site-Kopie nicht über diesen Pfad läuft.
**Seit 2026-08-04 ist das der Normalzustand:** Off-Site existiert, aber
pull-seitig (NAS → OneDrive, s. Kasten oben) statt push-seitig vom VPS. Der
B2-Vorschlag in diesem Abschnitt bleibt als Alternative dokumentiert, falls die
NAS-Kette je entfällt.

## 4. Prüfen, dass es tatsächlich läuft

Kurzcheck, jederzeit auf dem VPS ausführbar:

```sh
# Gibt es eine Datei von heute, und ist sie groesser als 0 Byte?
ls -lh /var/backups/emotepurge/ | tail -5

# Cron-Log auf Fehler pruefen (jede Zeile mit "FEHLER:" ist ein fehlgeschlagener Lauf):
grep FEHLER /var/log/emotepurge-backup.log
```

Erwartung: eine Datei mit heutigem Datum im Namen
(`emotepurge-<heutiges Datum>_<Uhrzeit>.sql.gz`), Größe plausibel im Vergleich zum
letzten Lauf (ein stark abweichend kleinerer Dump als üblich ist ein Warnsignal,
auch wenn das Skript selbst ihn nicht als Fehler wertet, solange er > 0 Byte ist).

Stand 2026-08-05: Die nachgelagerte Kette **ist** überwacht — NAS-Pull und
rclone-Upload pingen healthchecks.io (Erfolg bzw. sofort `/fail`; Cron-Schedules
mit Grace, Alarm per E-Mail), was sowohl „Job meldet Fehler" als auch „Job läuft
gar nicht mehr" abdeckt (Details im infra-docs-Bericht, Abschnitt Monitoring).
**Nicht abgedeckt ist der VPS-seitige `pg_dump`-Cron selbst:** liefert er keine
frischen Dumps mehr, zieht der Pull klaglos die alten Dateien und bleibt grün.
Für diesen Fall bleibt `grep FEHLER` auf dem VPS — oder ein vierter
healthchecks.io-Check am Ende der Cron-Zeile.

## 5. Konfigurierbare Umgebungsvariablen

| Variable | Default | Herkunft des Defaults |
|---|---|---|
| `POSTGRES_CONTAINER` | `emotepurge-postgres` | `container_name` in `docker-compose.prod.yml` (seit S3-38 heißt der Dev-Container `emotepurge-dev-postgres` — das Skript zielt auf Prod) |
| `POSTGRES_USER` | `emotepurge` | `POSTGRES_USER` in `.env.example` |
| `POSTGRES_DB` | `emotepurge` | `POSTGRES_DB` in beiden Compose-Dateien (hartkodiert, kein Env-Override im Repo) |
| `BACKUP_DIR` | `/var/backups/emotepurge` | Nicht aus dem Repo — Review-Vorschlag, s. o. |
| `RETENTION_DAYS` | `14` | Nicht aus dem Repo — Review-Vorschlag, s. o. |
| `BACKUP_FILE_PREFIX` | `emotepurge` | Frei gewählt, für die Rotations-Namensmuster-Prüfung |
| `OFFSITE_ENABLED` | `0` (aus) | — |
| `OFFSITE_RCLONE_REMOTE` | *(leer)* | Vom Nutzer zu setzen, s. Abschnitt 3 |

## 6. Offene Punkte / vom Nutzer zu prüfen

Aktualisiert 2026-08-05 nach der Einrichtung (VPS-Härtung, s. Kasten oben):

- ~~**`BACKUP_DIR=/var/backups/emotepurge`**~~ — **erledigt:** genau so
  eingerichtet, keine kollidierende Konvention. Der Sicherheitshinweis bleibt
  gültig: Ein Dump enthält potenziell sensible Daten (Twitch-Access-Tokens im
  `User`-Datensatz) — `chmod 700` auf das Verzeichnis ist kein optionales
  Detail; die NAS-Kopie ist per `setfacl` nur für den Pull-User lesbar.
- ~~**Off-Site-Ziel**~~ — **erledigt (anders als vorgeschlagen):** nicht
  B2-push vom VPS, sondern NAS-Pull + rclone-Crypt nach OneDrive (s. Kasten
  oben). Der B2-Abschnitt 3 bleibt als Alternative stehen.
- **VPS-SSH-Zugangsdaten** (`<VPS-USER>@<VPS-HOST>` in Abschnitt 1.1) — nicht im
  Repo hinterlegt, rein illustrativ. Seit 2026-08-04: sudo-User statt root.
- **Pfad zu `docker-compose.prod.yml` auf dem VPS** (Abschnitt 2.2) — unklar, ob
  die Datei dort überhaupt als lokale Datei existiert oder nur Portainer-intern
  verwaltet wird; ggf. per Portainer-UI redeployen statt `docker compose -f ...`.
- **Alerting** — seit 2026-08-05 weitgehend abgedeckt: NAS-Pull und
  rclone-Upload pingen healthchecks.io (E-Mail-Alarm bei Fehler oder Ausbleiben,
  s. Abschnitt 4). Restlücke: der VPS-seitige `pg_dump`-Cron selbst hat keinen
  eigenen Check — veraltete Dumps fallen erst im Pull-Log bzw. per `grep FEHLER`
  auf.
- **Retention-Grenzfall Downtime:** Läuft der VPS an einem geplanten Wartungstag
  keinen Cronjob, entsteht eine Backup-Lücke von einem Tag — bei
  `RETENTION_DAYS=14` unkritisch, bei kürzeren Werten ggf. relevant. Durch die
  30/60-Tage-Stufen auf NAS/OneDrive zusätzlich abgefedert.
