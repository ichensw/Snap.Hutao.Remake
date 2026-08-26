// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

namespace Snap.Hutao.Remastered.Service.MySqlSync;

using System;

public sealed class MySqlSyncOptions
{
    public const string ConnectionStringEnvironmentVariable = "HUTAO_MYSQL_CONNECTION_STRING";

    private const string BuiltInConnectionString = "Server=47.102.200.211;Port=3306;Database=snap_hutao_sync;User ID=root;Password=Csw20001024;SslMode=Preferred;AllowPublicKeyRetrieval=True;CharSet=utf8mb4;Connection Timeout=10;Default Command Timeout=60";

    public required string ConnectionString { get; init; }

    public static MySqlSyncOptions? FromEnvironment()
    {
        string? connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        return new()
        {
            ConnectionString = string.IsNullOrWhiteSpace(connectionString)
                ? BuiltInConnectionString
                : connectionString,
        };
    }
}
