using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PackagingStationPC.Models;

namespace PackagingStationPC.Services;

public sealed class SqliteProductionRepository : IProductionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly SnGenerator _snGenerator;

    public SqliteProductionRepository(string databasePath, SnGenerator snGenerator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _snGenerator = snGenerator;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS sn_counters (
                product_code TEXT NOT NULL,
                factory_code TEXT NOT NULL,
                package_year INTEGER NOT NULL,
                package_week INTEGER NOT NULL,
                next_sequence INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (product_code, factory_code, package_year, package_week)
            );
            CREATE TABLE IF NOT EXISTS production_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                package_sn TEXT NOT NULL UNIQUE,
                product_code TEXT NOT NULL,
                product_name TEXT NOT NULL,
                station_code TEXT NOT NULL,
                factory_code TEXT NOT NULL,
                package_year INTEGER NOT NULL,
                package_week INTEGER NOT NULL,
                sequence INTEGER NOT NULL,
                device_serial TEXT NOT NULL UNIQUE,
                version_snapshot_json TEXT NOT NULL,
                upload_status TEXT NOT NULL,
                upload_message TEXT NOT NULL DEFAULT '',
                print_status TEXT NOT NULL,
                print_message TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                uploaded_at TEXT NULL,
                printed_at TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS counter_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                product_code TEXT NOT NULL,
                factory_code TEXT NOT NULL,
                package_year INTEGER NOT NULL,
                package_week INTEGER NOT NULL,
                previous_sequence INTEGER NULL,
                next_sequence INTEGER NOT NULL,
                reason TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> GetNextSequenceAsync(
        SnParts key,
        int initialSequence,
        CancellationToken cancellationToken = default)
    {
        var normalized = key with { Sequence = initialSequence };
        _snGenerator.Validate(normalized);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureCounterAsync(connection, transaction, normalized, initialSequence, cancellationToken);
        var next = await ReadNextSequenceAsync(connection, transaction, normalized, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return next;
    }

    public async Task SetNextSequenceAsync(
        SnParts key,
        int nextSequence,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var parts = key with { Sequence = nextSequence };
        _snGenerator.Validate(parts);
        _ = _snGenerator.Generate(parts);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        await using (var usedSequenceCommand = CreateCounterCommand(connection, transaction, parts,
                         """
                         SELECT MAX(sequence) FROM production_records
                         WHERE product_code=$product AND factory_code=$factory AND package_year=$year AND package_week=$week;
                         """))
        {
            var value = await usedSequenceCommand.ExecuteScalarAsync(cancellationToken);
            if (value is not null and not DBNull &&
                nextSequence <= Convert.ToInt32(value, CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException(
                    $"该号段已使用到 {Convert.ToInt32(value, CultureInfo.InvariantCulture):000000}，下一流水号必须更大，不能回退复用。");
            }
        }

        int? previous = null;
        await using (var readCommand = CreateCounterCommand(connection, transaction, parts,
                         "SELECT next_sequence FROM sn_counters WHERE product_code=$product AND factory_code=$factory AND package_year=$year AND package_week=$week;"))
        {
            var value = await readCommand.ExecuteScalarAsync(cancellationToken);
            if (value is not null)
            {
                previous = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }

        await using (var updateCommand = CreateCounterCommand(connection, transaction, parts,
                         """
                         INSERT INTO sn_counters(product_code, factory_code, package_year, package_week, next_sequence, updated_at)
                         VALUES($product, $factory, $year, $week, $sequence, $now)
                         ON CONFLICT(product_code, factory_code, package_year, package_week)
                         DO UPDATE SET next_sequence=excluded.next_sequence, updated_at=excluded.updated_at;
                         """))
        {
            updateCommand.Parameters.AddWithValue("$sequence", nextSequence);
            updateCommand.Parameters.AddWithValue("$now", FormatDate(DateTimeOffset.Now));
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var eventCommand = CreateCounterCommand(connection, transaction, parts,
                         """
                         INSERT INTO counter_events(
                             product_code, factory_code, package_year, package_week,
                             previous_sequence, next_sequence, reason, created_at)
                         VALUES($product, $factory, $year, $week, $previous, $next, $reason, $now);
                         """))
        {
            eventCommand.Parameters.AddWithValue("$previous", previous is null ? DBNull.Value : previous.Value);
            eventCommand.Parameters.AddWithValue("$next", nextSequence);
            eventCommand.Parameters.AddWithValue("$reason", string.IsNullOrWhiteSpace(reason) ? "manual" : reason.Trim());
            eventCommand.Parameters.AddWithValue("$now", FormatDate(DateTimeOffset.Now));
            await eventCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ReservationResult> ReserveAsync(
        DeviceVersionSnapshot snapshot,
        string productName,
        string stationCode,
        SnParts requestedParts,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.DeviceSerial);
        _snGenerator.Validate(requestedParts);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        var existing = await GetByDeviceSerialAsync(connection, transaction, snapshot.DeviceSerial, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ReservationResult(existing, true);
        }

        await EnsureCounterAsync(connection, transaction, requestedParts, requestedParts.Sequence, cancellationToken);
        var nextSequence = await ReadNextSequenceAsync(connection, transaction, requestedParts, cancellationToken);
        if (nextSequence != requestedParts.Sequence)
        {
            throw new InvalidOperationException($"流水号已更新，当前可用流水号是 {nextSequence:000000}，请刷新后重试。");
        }

        var packageSn = _snGenerator.Generate(requestedParts);
        var now = DateTimeOffset.Now;
        long id;

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO production_records(
                    package_sn, product_code, product_name, station_code, factory_code, package_year,
                    package_week, sequence, device_serial, version_snapshot_json,
                    upload_status, print_status, created_at)
                VALUES(
                    $sn, $product, $productName, $stationCode, $factory, $year,
                    $week, $sequence, $deviceSerial, $snapshot,
                    $uploadStatus, $printStatus, $createdAt);
                SELECT last_insert_rowid();
                """;
            insertCommand.Parameters.AddWithValue("$sn", packageSn);
            insertCommand.Parameters.AddWithValue("$product", requestedParts.ProductCode);
            insertCommand.Parameters.AddWithValue("$productName", productName);
            insertCommand.Parameters.AddWithValue("$stationCode", stationCode);
            insertCommand.Parameters.AddWithValue("$factory", requestedParts.FactoryCode);
            insertCommand.Parameters.AddWithValue("$year", requestedParts.Year);
            insertCommand.Parameters.AddWithValue("$week", requestedParts.Week);
            insertCommand.Parameters.AddWithValue("$sequence", requestedParts.Sequence);
            insertCommand.Parameters.AddWithValue("$deviceSerial", snapshot.DeviceSerial);
            insertCommand.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(snapshot, JsonOptions));
            insertCommand.Parameters.AddWithValue("$uploadStatus", IntegrationStatus.Pending.ToString());
            insertCommand.Parameters.AddWithValue("$printStatus", IntegrationStatus.Pending.ToString());
            insertCommand.Parameters.AddWithValue("$createdAt", FormatDate(now));
            id = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        await using (var counterCommand = CreateCounterCommand(connection, transaction, requestedParts,
                         """
                         UPDATE sn_counters
                         SET next_sequence=$next, updated_at=$now
                         WHERE product_code=$product AND factory_code=$factory AND package_year=$year AND package_week=$week;
                         """))
        {
            counterCommand.Parameters.AddWithValue("$next", requestedParts.Sequence + 1);
            counterCommand.Parameters.AddWithValue("$now", FormatDate(now));
            await counterCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        var record = await GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("SN 已预留，但无法读取生产记录。");
        return new ReservationResult(record, false);
    }

    public async Task<ReservationResult> BindScannedPackageAsync(
        string packageCode,
        DeviceVersionSnapshot snapshot,
        string productCode,
        string productName,
        string stationCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.DeviceSerial);
        ArgumentException.ThrowIfNullOrWhiteSpace(productCode);
        packageCode = packageCode.Trim();

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var byPackage = await GetByPackageSnAsync(connection, transaction, packageCode, cancellationToken);
        var byDevice = await GetByDeviceSerialAsync(connection, transaction, snapshot.DeviceSerial, cancellationToken);

        if (byPackage is not null && !string.Equals(
                byPackage.DeviceSerial, snapshot.DeviceSerial, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"外包装盒码 {packageCode} 已绑定设备 {byPackage.DeviceSerial}，禁止重复绑定。");
        }

        if (byDevice is not null && !string.Equals(
                byDevice.PackageSn, packageCode, StringComparison.OrdinalIgnoreCase))
        {
            if (byDevice.UploadStatus == IntegrationStatus.Succeeded)
            {
                throw new InvalidOperationException($"设备 {snapshot.DeviceSerial} 已成功上传并绑定外包装盒码 {byDevice.PackageSn}，禁止修改。");
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE production_records
                SET package_sn=$sn, product_code=$product, product_name=$productName,
                    station_code=$stationCode, version_snapshot_json=$snapshot,
                    upload_status=$uploadStatus, upload_message='', uploaded_at=NULL
                WHERE id=$id;
                """;
            update.Parameters.AddWithValue("$sn", packageCode);
            update.Parameters.AddWithValue("$product", productCode.Trim());
            update.Parameters.AddWithValue("$productName", productName?.Trim() ?? string.Empty);
            update.Parameters.AddWithValue("$stationCode", stationCode?.Trim() ?? string.Empty);
            update.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(snapshot, JsonOptions));
            update.Parameters.AddWithValue("$uploadStatus", IntegrationStatus.Pending.ToString());
            update.Parameters.AddWithValue("$id", byDevice.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var rebound = await GetByIdAsync(byDevice.Id, cancellationToken)
                ?? throw new InvalidOperationException("扫码绑定已更新，但无法读取生产记录。");
            return new ReservationResult(rebound, false);
        }

        var existing = byPackage ?? byDevice;
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ReservationResult(existing, true);
        }

        var now = DateTimeOffset.Now;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO production_records(
                package_sn, product_code, product_name, station_code, factory_code, package_year,
                package_week, sequence, device_serial, version_snapshot_json,
                upload_status, print_status, created_at)
            VALUES(
                $sn, $product, $productName, $stationCode, '', 0,
                0, 0, $deviceSerial, $snapshot,
                $uploadStatus, $printStatus, $createdAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$sn", packageCode);
        command.Parameters.AddWithValue("$product", productCode.Trim());
        command.Parameters.AddWithValue("$productName", productName?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$stationCode", stationCode?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$deviceSerial", snapshot.DeviceSerial.Trim());
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(snapshot, JsonOptions));
        command.Parameters.AddWithValue("$uploadStatus", IntegrationStatus.Pending.ToString());
        command.Parameters.AddWithValue("$printStatus", IntegrationStatus.Pending.ToString());
        command.Parameters.AddWithValue("$createdAt", FormatDate(now));
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken);

        var record = await GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("扫码绑定已保存，但无法读取生产记录。");
        return new ReservationResult(record, false);
    }

    public async Task<ProductionRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM production_records WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    public Task UpdateUploadAsync(long id, IntegrationResult result, CancellationToken cancellationToken = default) =>
        UpdateIntegrationAsync(id, "upload", result, cancellationToken);

    public Task UpdatePrintAsync(long id, IntegrationResult result, CancellationToken cancellationToken = default) =>
        UpdateIntegrationAsync(id, "print", result, cancellationToken);

    public async Task<IReadOnlyList<ProductionRecord>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM production_records ORDER BY id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<ProductionRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    private async Task UpdateIntegrationAsync(
        long id,
        string prefix,
        IntegrationResult result,
        CancellationToken cancellationToken)
    {
        if (prefix is not ("upload" or "print"))
        {
            throw new ArgumentOutOfRangeException(nameof(prefix));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE production_records
            SET {prefix}_status=$status, {prefix}_message=$message, {prefix}ed_at=$completedAt
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$status", result.Success
            ? IntegrationStatus.Succeeded.ToString()
            : IntegrationStatus.Failed.ToString());
        command.Parameters.AddWithValue("$message", result.Message);
        command.Parameters.AddWithValue("$completedAt", result.Success ? FormatDate(DateTimeOffset.Now) : DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException($"生产记录 {id} 不存在。");
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task EnsureCounterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SnParts parts,
        int initialSequence,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCounterCommand(connection, transaction, parts,
            """
            INSERT INTO sn_counters(product_code, factory_code, package_year, package_week, next_sequence, updated_at)
            VALUES($product, $factory, $year, $week, $initial, $now)
            ON CONFLICT(product_code, factory_code, package_year, package_week) DO NOTHING;
            """);
        command.Parameters.AddWithValue("$initial", initialSequence);
        command.Parameters.AddWithValue("$now", FormatDate(DateTimeOffset.Now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ReadNextSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SnParts parts,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCounterCommand(connection, transaction, parts,
            "SELECT next_sequence FROM sn_counters WHERE product_code=$product AND factory_code=$factory AND package_year=$year AND package_week=$week;");
        var value = await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("无法读取流水号计数器。");
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static SqliteCommand CreateCounterCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SnParts parts,
        string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$product", parts.ProductCode);
        command.Parameters.AddWithValue("$factory", parts.FactoryCode);
        command.Parameters.AddWithValue("$year", parts.Year);
        command.Parameters.AddWithValue("$week", parts.Week);
        return command;
    }

    private static async Task<ProductionRecord?> GetByDeviceSerialAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string serial,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM production_records WHERE device_serial=$serial LIMIT 1;";
        command.Parameters.AddWithValue("$serial", serial);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    private static async Task<ProductionRecord?> GetByPackageSnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string packageSn,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM production_records WHERE package_sn=$sn LIMIT 1;";
        command.Parameters.AddWithValue("$sn", packageSn);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    private static ProductionRecord ReadRecord(SqliteDataReader reader)
    {
        var snapshot = JsonSerializer.Deserialize<DeviceVersionSnapshot>(
            reader.GetString(reader.GetOrdinal("version_snapshot_json")), JsonOptions) ?? new DeviceVersionSnapshot();
        return new ProductionRecord
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            PackageSn = reader.GetString(reader.GetOrdinal("package_sn")),
            ProductCode = reader.GetString(reader.GetOrdinal("product_code")),
            ProductName = reader.GetString(reader.GetOrdinal("product_name")),
            StationCode = reader.GetString(reader.GetOrdinal("station_code")),
            FactoryCode = reader.GetString(reader.GetOrdinal("factory_code")),
            PackageYear = reader.GetInt32(reader.GetOrdinal("package_year")),
            PackageWeek = reader.GetInt32(reader.GetOrdinal("package_week")),
            Sequence = reader.GetInt32(reader.GetOrdinal("sequence")),
            DeviceSerial = reader.GetString(reader.GetOrdinal("device_serial")),
            VersionSnapshot = snapshot,
            UploadStatus = ParseStatus(reader.GetString(reader.GetOrdinal("upload_status"))),
            UploadMessage = reader.GetString(reader.GetOrdinal("upload_message")),
            PrintStatus = ParseStatus(reader.GetString(reader.GetOrdinal("print_status"))),
            PrintMessage = reader.GetString(reader.GetOrdinal("print_message")),
            CreatedAt = ParseDate(reader.GetString(reader.GetOrdinal("created_at"))),
            UploadedAt = ReadNullableDate(reader, "uploaded_at"),
            PrintedAt = ReadNullableDate(reader, "printed_at")
        };
    }

    private static IntegrationStatus ParseStatus(string value) =>
        Enum.TryParse<IntegrationStatus>(value, out var status) ? status : IntegrationStatus.Pending;

    private static string FormatDate(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));
    }
}
