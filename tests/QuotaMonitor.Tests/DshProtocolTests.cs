using System.Text;
using QuotaMonitor.Core.Models;
using QuotaMonitor.Core.Services;

namespace QuotaMonitor.Tests;
public class DshProtocolTests
{
    private const string Valid="""{"protocol":1,"sourceVersion":"0.2.1-rc.6","state":"ready","observedAt":"2026-11-03T01:00:00.000Z","normal_wallets":[{"currency":"CNY","balance":"1.123456789012345678901234567890"}],"bonus_wallets":[]}""";
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-03T01:00:00Z");
    [Theory]
    [InlineData("\"protocol\":1","\"protocol\":2")]
    [InlineData("\"protocol\":1","\"protocol\":\"1\"")]
    [InlineData("\"protocol\":1","\"protocol\":1,\"protocol\":1")]
    [InlineData("\"protocol\":1","\"protocol\":1,\"extra\":true")]
    [InlineData("\"state\":\"ready\"","\"state\":\"forged\"")]
    [InlineData("\"state\":\"ready\"","\"state\":\"ready\",\"queryStatus\":\"failed\"")]
    [InlineData("\"state\":\"ready\"","\"state\":\"stale\",\"queryStatus\":\"ready\"")]
    [InlineData("\"state\":\"ready\"","\"state\":\"signed-out\",\"queryStatus\":\"ready\"")]
    [InlineData("\"sourceVersion\":\"0.2.1-rc.6\"","\"sourceVersion\":null")]
    [InlineData("\"sourceVersion\":\"0.2.1-rc.6\"","\"sourceVersion\":123")]
    [InlineData("\"0.2.1-rc.6\"","\"host\\nforged\"")]
    [InlineData("2026-11-03T01:00:00.000Z","2026-11-03")]
    [InlineData("2026-11-03T01:00:00.000Z","2026-11-03T02:00:00Z")]
    [InlineData("2026-11-03T01:00:00.000Z","0001-01-01T00:00:00Z")]
    [InlineData("\"normal_wallets\":[{\"currency\":\"CNY\",\"balance\":\"1.123456789012345678901234567890\"}]","\"normal_wallets\":null")]
    [InlineData("\"currency\":\"CNY\"","\"currency\":\"CNY\",\"currency\":\"CNY\"")]
    [InlineData("\"balance\":\"1.123456789012345678901234567890\"","\"balance\":\"NaN\"")]
    [InlineData("\"balance\":\"1.123456789012345678901234567890\"","\"balance\":\"1e999999999\"")]
    [InlineData("\"balance\":\"1.123456789012345678901234567890\"","\"balance\":\"١٢\"")]
    [InlineData("\"balance\":\"1.123456789012345678901234567890\"","\"balance\":0")]
    [InlineData("\"balance\":\"1.123456789012345678901234567890\"","\"balance\":\"1\\n\"")]
    [InlineData("\"0.2.1-rc.6\"","\"0.2.1-rc.6\\n\"")]
    public void MalformedMessagesFailClosed(string before,string after)=>Assert.Throws<InvalidDataException>(()=>DshBalanceReceiver.Parse(Encoding.UTF8.GetBytes(Valid.Replace(before,after)),DshBalanceReading.Empty,Now));
    [Theory]
    [InlineData("signed-out")][InlineData("failed")][InlineData("unavailable")][InlineData("loading")]
    public void InvalidIgnoredFieldsCannotBypassValidation(string state)
    {
        var json=Valid.Replace("\"ready\"",$"\"{state}\"").Replace("2026-11-03T01:00:00.000Z","invalid");
        Assert.Throws<InvalidDataException>(()=>DshBalanceReceiver.Parse(Encoding.UTF8.GetBytes(json),DshBalanceReading.Empty,Now));
    }
    [Fact] public void VersionLengthAndDeepNestingAreBounded()
    {
        Assert.Throws<InvalidDataException>(()=>DshBalanceReceiver.Parse(Encoding.UTF8.GetBytes(Valid.Replace("0.2.1-rc.6",new string('1',129))),DshBalanceReading.Empty,Now));
        Assert.Throws<InvalidDataException>(()=>DshBalanceReceiver.Parse(Encoding.UTF8.GetBytes(new string('[',20)+"1"+new string(']',20)),DshBalanceReading.Empty,Now));
    }
    [Theory][InlineData("1e1000")][InlineData("1e-1000")][InlineData("-1.123456789012345678901234567890")]
    public void AmountPrecisionIsPreserved(string amount)
    {
        var r=DshBalanceReceiver.Parse(Encoding.UTF8.GetBytes(Valid.Replace("1.123456789012345678901234567890",amount)),DshBalanceReading.Empty,Now);
        Assert.Equal(amount,r.Balance!.NormalWallets[0].Balance);
    }
}
