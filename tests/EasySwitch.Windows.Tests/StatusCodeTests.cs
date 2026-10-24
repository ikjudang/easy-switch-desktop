using System.IO;
using System.Text;
using EasySwitch.Core;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Tests;

public sealed class StatusCodeTests
{
    [Fact]
    public void CodeExportCopiesTheEntireFileRatherThanTheSixHundredVisibleRows()
    {
        var root=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));
        try
        {
            using var log=new AppLog(root);log.Write("PRIVATE DEVICE RAW FRAME");
            for(var i=0;i<725;i++)log.WriteCode($"{SwitchCode.Prefix}|R{i:D4}|M1|END|U|LC|E{i}");
            var exported=Path.Combine(root,"export.codes.txt");log.ExportCodes(exported);
            using(var source=new FileStream(log.CodeFilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
            using(var copy=new MemoryStream())
            {source.CopyTo(copy);Assert.Equal(copy.ToArray(),File.ReadAllBytes(exported));}
            Assert.Equal(725,File.ReadAllLines(exported).Length);Assert.Equal(600,log.RecentCodes().Count);
            Assert.DoesNotContain("PRIVATE DEVICE",File.ReadAllText(exported));
            Assert.Throws<IOException>(()=>log.ExportCodes(log.CodeFilePath));
            Assert.Throws<IOException>(()=>log.ExportCodes(log.FilePath));
            using var active=new FileStream(log.CodeFilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            using var reader=new StreamReader(active);Assert.Equal(725,reader.ReadToEnd().Split('\n',StringSplitOptions.RemoveEmptyEntries).Length);
        }
        finally
        {
            var parent=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"EasySwitch.Tests"))+Path.DirectorySeparatorChar;
            var target=Path.GetFullPath(root);
            if(target.StartsWith(parent,StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))Directory.Delete(target,true);
        }
    }
    [Theory]
    [InlineData(SwitchOutcome.AlreadyCurrent,"S AC")]
    [InlineData(SwitchOutcome.TargetReported,"S TR")]
    [InlineData(SwitchOutcome.LocalLinkClosed,"U LC")]
    [InlineData(SwitchOutcome.Unverified,"U UV")]
    [InlineData(SwitchOutcome.StillOnSource,"F SS")]
    [InlineData(SwitchOutcome.Failed,"F ER")]
    [InlineData(SwitchOutcome.Cancelled,"C CA")]
    public void OutcomeCodesDistinguishLocalDepartureUnknownFailureAndCancellation(SwitchOutcome outcome,string expected)
    {
        var p=new SwitchProgress(SwitchStep.Ended,2,2,1420,0,0,outcome,SwitchEndReason.RetryLimit,3,21,90,2);
        var code=SwitchCode.Format(4,"F",1,"M1",1800,p);
        Assert.Contains($"END|{expected.Replace(' ','|')}",code);Assert.Contains("R0004|F2|M1",code);
        Assert.Contains("E1800|D1420|I0|Q3|WT21|QT90|A2|N2|RT1|W2|H1",code);
        Assert.Matches(@"\AES1\|V0213\|[A-Z0-9|]+\z",code);
    }
    [Fact]
    public void DeviceIdentifiersCannotBeUsedAsRoleCodes()
    {
        var progress=new SwitchProgress(SwitchStep.Written,1,1,20,10);
        Assert.Throws<ArgumentException>(()=>SwitchCode.Format(1,"F",1,"MX_MASTER_4",20,progress));
        Assert.Throws<ArgumentException>(()=>SwitchCode.Format(1,"046D",1,"M1",20,progress));
    }
    [Fact]
    public void CodeFileIsIndependentFlushedAndSurvivesRawFrameFlood()
    {
        var root=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));
        try
        {
            using var log=new AppLog(root);
            log.Write("PRIVATE_DEVICE_NAME 046D AABBCC");log.WriteCode("ES1 V022 R0001 F2 M1 END U LC E1320");
            for(var i=0;i<700;i++)log.Write($"RX {i}");
            using var stream=new FileStream(log.CodeFilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            using var reader=new StreamReader(stream,Encoding.UTF8);var code=reader.ReadToEnd();
            Assert.Contains("END|U|LC",code);Assert.DoesNotContain("PRIVATE_DEVICE_NAME",code);Assert.DoesNotContain("AABBCC",code);Assert.DoesNotContain("RX",code);
            Assert.DoesNotContain(' ',code.Trim());Assert.Equal(10,code.Trim().Split('|').Length);
            Assert.Matches(@"\d{8}T\d{6}\.\d{3}[+-]\d{2}:\d{2}",code);Assert.Single(log.RecentCodes());
            Assert.NotEqual(log.FilePath,log.CodeFilePath);Assert.Equal(600,log.Recent().Count);
            Assert.Throws<ArgumentException>(()=>log.WriteCode("한글 이름 또는 주소"));
        }
        finally{Directory.Delete(root,true);}
    }
}
