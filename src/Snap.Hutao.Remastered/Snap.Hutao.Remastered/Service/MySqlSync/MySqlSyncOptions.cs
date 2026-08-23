// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

namespace Snap.Hutao.Remastered.Service.MySqlSync;

using System;

public sealed class MySqlSyncOptions
{
    public const string ConnectionStringEnvironmentVariable = "HUTAO_MYSQL_CONNECTION_STRING";

    public required string ConnectionString { get; init; }

    public static MySqlSyncOptions? FromEnvironment()
    {
        string? connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        return string.IsNullOrWhiteSpace(connectionString)
            ? null
            : new() { ConnectionString = connectionString };
    }
}
