SELECT
    @@SERVERNAME AS ServerName,
    SERVERPROPERTY('ProductVersion') AS Version,
    SERVERPROPERTY('Edition') AS Edition,
    DB_NAME() AS CurrentDatabase;