#!/usr/bin/env python3
"""Back up WhipRadio PostgreSQL data to SQLite and restore PostgreSQL from SQLite.

This is a developer recovery utility, not a runtime application feature. It uses
only Python's standard library plus Docker/psql from the running Postgres
container, so it does not add package dependencies to WhipRadio itself.
"""

from __future__ import annotations

import argparse
import csv
import io
import json
import os
import sqlite3
import subprocess
import sys
from collections.abc import Iterable, Sequence
from pathlib import Path
from typing import Any


APP_TABLES_IN_ORDER = [
    "Moderators",
    "Artists",
    "ArtistMembers",
    "Formats",
    "Tracks",
    "Announcements",
    "TalkBits",
    "TalkBreaks",
    "Jingles",
    "NewsFeeds",
    "Studios",
    "StationSettings",
    "ProgramSlots",
    "TalkBitRenditions",
    "TalkParts",
    "Votes",
    "ArtistPosts",
    "ModeratorMemories",
    "ListenerMessages",
    "StudioHistory",
    "MediaAnalyses",
    "TransitionLog",
    "NewsItems",
    "NewsPackages",
    "PlayLog",
]

SKIP_TABLES = {"__EFMigrationsHistory", "__EFMigrationsLock", "sqlite_sequence"}
NUMERIC_TYPES = {"integer", "bigint", "smallint", "double precision", "real", "numeric"}
BOOLEAN_TYPES = {"boolean"}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--docker-config",
        help="Optional Docker config directory, useful when the normal user profile config is inaccessible.",
    )
    parser.add_argument("--container", help="Postgres container name. Auto-detected when omitted.")
    parser.add_argument("--database", default="radio", help="Postgres database name.")
    parser.add_argument("--user", default="postgres", help="Postgres user.")
    parser.add_argument("--password", help="Postgres password. Auto-detected from container env when omitted.")

    subparsers = parser.add_subparsers(dest="command", required=True)

    backup = subparsers.add_parser("backup", help="Export app tables from Postgres into a SQLite file.")
    backup.add_argument("--out", required=True, help="SQLite output file.")
    backup.add_argument("--overwrite", action="store_true", help="Replace an existing SQLite output file.")

    restore = subparsers.add_parser("restore", help="Import app tables from a SQLite file into Postgres.")
    restore.add_argument("--source", required=True, help="SQLite source file.")
    restore.add_argument("--truncate", action="store_true", help="Truncate app tables before importing.")
    restore.add_argument("--dry-run", action="store_true", help="Inspect only; do not modify Postgres.")
    restore.add_argument(
        "--table",
        action="append",
        dest="tables",
        help="Restore only this table. Can be specified multiple times.",
    )

    args = parser.parse_args()
    docker_env = docker_environment(args.docker_config)
    container = args.container or detect_postgres_container(docker_env)
    password = args.password or read_postgres_password(container, docker_env)
    pg = Postgres(container, args.database, args.user, password, docker_env)

    if args.command == "backup":
        backup_postgres_to_sqlite(pg, Path(args.out), args.overwrite)
    elif args.command == "restore":
        restore_sqlite_to_postgres(
            pg,
            Path(args.source),
            truncate=args.truncate,
            dry_run=args.dry_run,
            requested_tables=args.tables,
        )
    else:
        parser.error(f"Unknown command {args.command}")

    return 0


def docker_environment(docker_config: str | None) -> dict[str, str]:
    env = os.environ.copy()
    if docker_config:
        env["DOCKER_CONFIG"] = docker_config
    return env


def run(args: Sequence[str], *, env: dict[str, str], input_text: str | None = None) -> str:
    completed = subprocess.run(
        args,
        input=input_text,
        text=True,
        encoding="utf-8",
        errors="replace",
        capture_output=True,
        env=env,
        check=False,
    )
    if completed.returncode != 0:
        raise RuntimeError(
            "Command failed:\n"
            + " ".join(args)
            + "\nSTDOUT:\n"
            + completed.stdout
            + "\nSTDERR:\n"
            + completed.stderr
        )
    return completed.stdout


def detect_postgres_container(env: dict[str, str]) -> str:
    output = run(["docker", "ps", "--format", "{{.Names}}\t{{.Image}}"], env=env)
    candidates: list[str] = []
    for line in output.splitlines():
        if not line.strip():
            continue
        name, image = line.split("\t", 1)
        if "postgres" in image.lower():
            candidates.append(name)

    if len(candidates) != 1:
        raise RuntimeError(
            "Could not uniquely detect a running Postgres container. "
            f"Candidates: {', '.join(candidates) or '(none)'}. Pass --container."
        )

    return candidates[0]


def read_postgres_password(container: str, env: dict[str, str]) -> str:
    raw = run(["docker", "inspect", container, "--format", "{{json .Config.Env}}"], env=env)
    values = json.loads(raw)
    for value in values:
        if value.startswith("POSTGRES_PASSWORD="):
            return value.split("=", 1)[1]
    raise RuntimeError("POSTGRES_PASSWORD was not found in the Postgres container environment.")


class Postgres:
    def __init__(
        self,
        container: str,
        database: str,
        user: str,
        password: str,
        docker_env: dict[str, str],
    ) -> None:
        self.container = container
        self.database = database
        self.user = user
        self.password = password
        self.docker_env = docker_env

    def psql_args(self) -> list[str]:
        return [
            "docker",
            "exec",
            "-i",
            "-e",
            f"PGPASSWORD={self.password}",
            self.container,
            "psql",
            "-v",
            "ON_ERROR_STOP=1",
            "-U",
            self.user,
            "-d",
            self.database,
        ]

    def query_tsv(self, sql: str) -> list[list[str]]:
        args = self.psql_args() + ["-q", "-A", "-t", "-F", "\t", "-c", sql]
        output = run(args, env=self.docker_env)
        rows = []
        for line in output.splitlines():
            if line:
                rows.append(line.split("\t"))
        return rows

    def execute(self, sql: str) -> None:
        run(self.psql_args(), input_text=sql, env=self.docker_env)

    def copy_csv(self, table: str, columns: Sequence[str]) -> str:
        projection = ", ".join(quote_ident(column) for column in columns)
        sql = (
            f"COPY (SELECT {projection} FROM {quote_ident(table)} ORDER BY 1) "
            "TO STDOUT WITH (FORMAT CSV, NULL '\\N')"
        )
        args = self.psql_args() + ["-q", "-c", sql]
        return run(args, env=self.docker_env)


def backup_postgres_to_sqlite(pg: Postgres, out_path: Path, overwrite: bool) -> None:
    if out_path.exists() and not overwrite:
        raise RuntimeError(f"{out_path} already exists. Pass --overwrite to replace it.")

    if out_path.exists():
        out_path.unlink()
    out_path.parent.mkdir(parents=True, exist_ok=True)

    metadata = load_postgres_metadata(pg)
    tables = [table for table in APP_TABLES_IN_ORDER if table in metadata]

    con = sqlite3.connect(out_path)
    try:
        cur = con.cursor()
        counts: dict[str, int] = {}
        for table in tables:
            columns = [column.name for column in metadata[table]]
            create_sql = ", ".join(f'"{column}" TEXT' for column in columns)
            cur.execute(f'CREATE TABLE "{table}" ({create_sql})')

            csv_text = pg.copy_csv(table, columns)
            reader = csv.reader(io.StringIO(csv_text))
            rows = [[None if value == "\\N" else value for value in row] for row in reader]
            if rows:
                placeholders = ", ".join("?" for _ in columns)
                column_list = ", ".join(f'"{column}"' for column in columns)
                cur.executemany(
                    f'INSERT INTO "{table}" ({column_list}) VALUES ({placeholders})',
                    rows,
                )
            counts[table] = len(rows)
        con.commit()
    finally:
        con.close()

    print_counts(f"Backed up Postgres to {out_path}", counts)


def restore_sqlite_to_postgres(
    pg: Postgres,
    source_path: Path,
    *,
    truncate: bool,
    dry_run: bool,
    requested_tables: Sequence[str] | None,
) -> None:
    if not source_path.exists():
        raise RuntimeError(f"SQLite source does not exist: {source_path}")

    metadata = load_postgres_metadata(pg)
    source_tables = read_sqlite_tables(source_path)

    tables = [table for table in APP_TABLES_IN_ORDER if table in metadata and table in source_tables]
    if requested_tables:
        requested = set(requested_tables)
        unknown = requested - set(tables)
        if unknown:
            raise RuntimeError(f"Requested table(s) are not importable: {', '.join(sorted(unknown))}")
        tables = [table for table in tables if table in requested]

    counts = read_sqlite_counts(source_path, tables)
    print_counts(f"SQLite source {source_path}", counts)
    missing_tables = [table for table in APP_TABLES_IN_ORDER if table in metadata and table not in source_tables]
    if missing_tables:
        print("Skipping missing source tables: " + ", ".join(missing_tables))

    validate_restore_columns(source_path, metadata, tables)

    if dry_run:
        print("Dry run only; Postgres was not modified.")
        return

    if not truncate:
        raise RuntimeError("Restore refuses to append by default. Pass --truncate to replace app tables.")

    truncate_tables(pg, tables)
    import_tables(pg, source_path, metadata, tables)
    reset_identity_sequences(pg)

    after = load_postgres_counts(pg, tables)
    print_counts("Restored Postgres row counts", after)


def load_postgres_metadata(pg: Postgres) -> dict[str, list["PgColumn"]]:
    rows = pg.query_tsv(
        """
        select table_name, column_name, data_type, is_nullable, is_identity
        from information_schema.columns
        where table_schema = 'public'
        order by table_name, ordinal_position;
        """
    )
    metadata: dict[str, list[PgColumn]] = {}
    for table, column, data_type, nullable, identity in rows:
        if table in SKIP_TABLES:
            continue
        metadata.setdefault(table, []).append(
            PgColumn(
                table=table,
                name=column,
                data_type=data_type,
                is_nullable=nullable == "YES",
                is_identity=identity == "YES",
            )
        )
    return metadata


def read_sqlite_tables(path: Path) -> set[str]:
    con = sqlite3.connect(path)
    try:
        rows = con.execute(
            "select name from sqlite_master where type = 'table' and name not like 'sqlite_%'"
        ).fetchall()
        return {row[0] for row in rows if row[0] not in SKIP_TABLES}
    finally:
        con.close()


def read_sqlite_columns(con: sqlite3.Connection, table: str) -> set[str]:
    rows = con.execute(f'PRAGMA table_info("{table}")').fetchall()
    return {row[1] for row in rows}


def validate_restore_columns(
    source_path: Path,
    metadata: dict[str, list["PgColumn"]],
    tables: Sequence[str],
) -> None:
    con = sqlite3.connect(source_path)
    try:
        for table in tables:
            source_columns = read_sqlite_columns(con, table)
            missing_required = [
                column.name
                for column in metadata[table]
                if column.name not in source_columns and not column.is_nullable and not column.is_identity
            ]
            if missing_required:
                raise RuntimeError(
                    f"{table} is missing required target column(s): {', '.join(missing_required)}"
                )
    finally:
        con.close()


def read_sqlite_counts(path: Path, tables: Sequence[str]) -> dict[str, int]:
    con = sqlite3.connect(path)
    try:
        return {table: con.execute(f'SELECT COUNT(*) FROM "{table}"').fetchone()[0] for table in tables}
    finally:
        con.close()


def load_postgres_counts(pg: Postgres, tables: Sequence[str]) -> dict[str, int]:
    selects = [
        f"select {sql_literal(table)}, count(*)::bigint from {quote_ident(table)}"
        for table in tables
    ]
    rows = pg.query_tsv(" union all ".join(selects) + ";")
    return {table: int(count) for table, count in rows}


def truncate_tables(pg: Postgres, tables: Sequence[str]) -> None:
    if not tables:
        return
    table_list = ", ".join(quote_ident(table) for table in tables)
    pg.execute(f"TRUNCATE TABLE {table_list} RESTART IDENTITY CASCADE;\n")


def import_tables(
    pg: Postgres,
    source_path: Path,
    metadata: dict[str, list["PgColumn"]],
    tables: Sequence[str],
) -> None:
    con = sqlite3.connect(source_path)
    con.row_factory = sqlite3.Row
    try:
        for table in tables:
            source_columns = read_sqlite_columns(con, table)
            columns = [column for column in metadata[table] if column.name in source_columns]
            missing_required = [
                column.name
                for column in metadata[table]
                if column.name not in source_columns and not column.is_nullable and not column.is_identity
            ]
            if missing_required:
                raise RuntimeError(
                    f"{table} is missing required target column(s): {', '.join(missing_required)}"
                )

            rows = con.execute(
                f'SELECT {", ".join(quote_ident_sqlite(column.name) for column in columns)} FROM "{table}"'
            )
            batch: list[sqlite3.Row] = []
            imported = 0
            for row in rows:
                batch.append(row)
                if len(batch) >= 250:
                    pg.execute(build_insert_sql(table, columns, batch))
                    imported += len(batch)
                    batch.clear()
            if batch:
                pg.execute(build_insert_sql(table, columns, batch))
                imported += len(batch)
            print(f"Imported {imported} row(s) into {table}")
    finally:
        con.close()


def build_insert_sql(table: str, columns: Sequence["PgColumn"], rows: Sequence[sqlite3.Row]) -> str:
    if not rows:
        return ""
    column_list = ", ".join(quote_ident(column.name) for column in columns)
    values = []
    for row in rows:
        values.append(
            "(" + ", ".join(to_pg_literal(row[index], columns[index]) for index in range(len(columns))) + ")"
        )
    return f"INSERT INTO {quote_ident(table)} ({column_list}) VALUES\n" + ",\n".join(values) + ";\n"


def reset_identity_sequences(pg: Postgres) -> None:
    pg.execute(
        """
        DO $$
        DECLARE
            r record;
            max_id bigint;
        BEGIN
            FOR r IN
                SELECT table_name, column_name,
                       pg_get_serial_sequence(format('%I', table_name), column_name) AS sequence_name
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND is_identity = 'YES'
            LOOP
                IF r.sequence_name IS NULL THEN
                    CONTINUE;
                END IF;

                EXECUTE format('SELECT COALESCE(MAX(%I), 0) FROM %I', r.column_name, r.table_name)
                    INTO max_id;

                IF max_id > 0 THEN
                    EXECUTE format('SELECT setval(%L, %s, true)', r.sequence_name, max_id);
                ELSE
                    EXECUTE format('SELECT setval(%L, 1, false)', r.sequence_name);
                END IF;
            END LOOP;
        END $$;
        """
    )


def to_pg_literal(value: Any, column: "PgColumn") -> str:
    if value is None:
        return "NULL"

    if column.data_type in BOOLEAN_TYPES:
        if isinstance(value, bool):
            return "TRUE" if value else "FALSE"
        text = str(value).strip().lower()
        if text in {"1", "true", "t", "yes", "y"}:
            return "TRUE"
        if text in {"0", "false", "f", "no", "n"}:
            return "FALSE"
        raise RuntimeError(f"Cannot convert {value!r} to boolean for {column.table}.{column.name}")

    if column.data_type in NUMERIC_TYPES:
        text = str(value).strip()
        if text == "":
            return "NULL"
        return text

    return sql_literal(str(value))


def quote_ident(identifier: str) -> str:
    return '"' + identifier.replace('"', '""') + '"'


def quote_ident_sqlite(identifier: str) -> str:
    return '"' + identifier.replace('"', '""') + '"'


def sql_literal(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def print_counts(title: str, counts: dict[str, int]) -> None:
    print(title)
    for table, count in counts.items():
        print(f"  {table}: {count}")


class PgColumn:
    def __init__(
        self,
        *,
        table: str,
        name: str,
        data_type: str,
        is_nullable: bool,
        is_identity: bool,
    ) -> None:
        self.table = table
        self.name = name
        self.data_type = data_type
        self.is_nullable = is_nullable
        self.is_identity = is_identity


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(1)
