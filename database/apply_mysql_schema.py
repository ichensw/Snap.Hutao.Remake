import os
from pathlib import Path

import pymysql


schema = Path(__file__).with_name("mysql_schema.sql").read_text(encoding="utf-8")

connection = pymysql.connect(
    host=os.environ["HUTAO_MYSQL_HOST"],
    port=int(os.environ.get("HUTAO_MYSQL_PORT", "3306")),
    user=os.environ["HUTAO_MYSQL_USER"],
    password=os.environ["HUTAO_MYSQL_PASSWORD"],
    charset="utf8mb4",
    autocommit=True,
    connect_timeout=10,
    read_timeout=30,
    write_timeout=30,
)

try:
    with connection.cursor() as cursor:
        for statement in schema.split(";"):
            statement = statement.strip()
            if statement:
                cursor.execute(statement)

        cursor.execute(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=%s",
            ("snap_hutao_sync",),
        )
        table_count = cursor.fetchone()[0]

        cursor.execute("SHOW TABLES FROM snap_hutao_sync")
        tables = [row[0] for row in cursor.fetchall()]

    print(f"tables={table_count}")
    print("\n".join(tables))
finally:
    connection.close()
