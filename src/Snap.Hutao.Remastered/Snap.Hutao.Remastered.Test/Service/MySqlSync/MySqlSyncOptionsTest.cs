using Microsoft.VisualStudio.TestTools.UnitTesting;
using Snap.Hutao.Remastered.Service.MySqlSync;
using System;

namespace Snap.Hutao.Remastered.Test.Service.MySqlSync;

[TestClass]
public sealed class MySqlSyncOptionsTest
{
    [TestMethod]
    public void FromEnvironmentReturnsBuiltInConnectionStringWhenEnvironmentIsEmpty()
    {
        using EnvironmentVariableScope scope = new(MySqlSyncOptions.ConnectionStringEnvironmentVariable, null);

        MySqlSyncOptions? options = MySqlSyncOptions.FromEnvironment();

        Assert.IsNotNull(options);
        StringAssert.Contains(options.ConnectionString, "snap_hutao_sync");
    }

    [TestMethod]
    public void FromEnvironmentReturnsConnectionStringWhenConfigured()
    {
        using EnvironmentVariableScope scope = new(MySqlSyncOptions.ConnectionStringEnvironmentVariable, "Server=127.0.0.1;");

        MySqlSyncOptions? options = MySqlSyncOptions.FromEnvironment();

        Assert.IsNotNull(options);
        Assert.AreEqual("Server=127.0.0.1;", options.ConnectionString);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string name;
        private readonly string? oldValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            this.name = name;
            oldValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(name, oldValue);
        }
    }
}
