using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Windows.Network;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class TcpConnectionMigratorTests
{
    [Theory]
    [InlineData("yysls.exe", @"D:\\Games\\yysls.exe", "yysls.exe", true)]
    [InlineData("helper.exe", @"C:\\Apps\\baidu\\helper.exe", @"baidu\*.exe", true)]
    [InlineData("helper.dll", @"C:\\Apps\\baidu\\helper.dll", @"baidu\*.exe", false)]
    [InlineData("helper.exe", @"C:\\Apps\\other\\helper.exe", @"baidu\*.exe", false)]
    public void GlobMatchesNameAndPath(string name, string path, string pattern, bool expected)
    {
        Assert.Equal(expected, TcpConnectionMigrator.GlobMatches(name, path, pattern));
    }

    [Fact]
    public void AllowAndDenyModesInvertTheSamePatternList()
    {
        var patterns = new[] { "yysls.exe" };

        Assert.True(TcpConnectionMigrator.ShouldClose(
            "yysls.exe", null, ConnectionCutMode.AllowList, patterns));
        Assert.False(TcpConnectionMigrator.ShouldClose(
            "browser.exe", null, ConnectionCutMode.AllowList, patterns));
        Assert.False(TcpConnectionMigrator.ShouldClose(
            "yysls.exe", null, ConnectionCutMode.DenyList, patterns));
        Assert.True(TcpConnectionMigrator.ShouldClose(
            "browser.exe", null, ConnectionCutMode.DenyList, patterns));
    }
}
