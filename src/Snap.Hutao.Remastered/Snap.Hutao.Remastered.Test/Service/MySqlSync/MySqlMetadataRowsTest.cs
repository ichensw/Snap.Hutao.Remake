using Microsoft.VisualStudio.TestTools.UnitTesting;
using Snap.Hutao.Remastered.Service.MySqlSync;

namespace Snap.Hutao.Remastered.Test.Service.MySqlSync;

[TestClass]
public sealed class MySqlMetadataRowsTest
{
    [TestMethod]
    public void EnumRowsSkipZeroValue()
    {
        MySqlMetadataRows.EnumRow[] rows = [.. MySqlMetadataRows.CreateEnumRows<TestKind>("zh-cn")];

        Assert.AreEqual(1, rows.Length);
        Assert.AreEqual(10, rows[0].Value);
        Assert.AreEqual(nameof(TestKind.RealValue), rows[0].Name);
        Assert.AreEqual("zh-cn", rows[0].Lang);
    }

    private enum TestKind
    {
        None = 0,
        RealValue = 10,
    }
}
