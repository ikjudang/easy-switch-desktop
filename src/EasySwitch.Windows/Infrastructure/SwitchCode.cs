using EasySwitch.Core;

namespace EasySwitch.Windows.Infrastructure;

public static class SwitchCode
{
    public const string Prefix="ES1|V0213";
    public static string Format(int request,string source,int target,string role,long elapsed,SwitchProgress p)
    {
        if(source is not ("F" or "B" or "H" or "T" or "P") || target is <0 or >2 || !System.Text.RegularExpressions.Regex.IsMatch(role,@"\A[KMD][1-9][0-9]*\z"))throw new ArgumentException("Invalid fixed status-code fields");
        var stage=p.Step switch
        {
            SwitchStep.Started=>"RUN",SwitchStep.Joined=>"JON",SwitchStep.WriteStarted=>"SND",SwitchStep.Written=>"WRT",SwitchStep.VerifyStarted=>"CHK",
            SwitchStep.TargetReported=>"TGT",SwitchStep.SourceReported=>"SRC",SwitchStep.NoReply=>"NRP",SwitchStep.CommunicationError=>"IOE",_=>"END",
        };
        var outcome=p.Outcome switch
        {
            SwitchOutcome.AlreadyCurrent=>"AC",SwitchOutcome.TargetReported=>"TR",SwitchOutcome.LocalLinkClosed=>"LC",SwitchOutcome.Unverified=>"UV",
            SwitchOutcome.StillOnSource=>"SS",SwitchOutcome.Cancelled=>"CA",SwitchOutcome.Failed=>"ER",_=>"NA",
        };
        var verdict=p.Outcome switch
        {
            SwitchOutcome.AlreadyCurrent or SwitchOutcome.TargetReported=>"S",
            SwitchOutcome.LocalLinkClosed or SwitchOutcome.Unverified=>"U",
            SwitchOutcome.StillOnSource or SwitchOutcome.Failed=>"F",SwitchOutcome.Cancelled=>"C",_=>"P",
        };
        var reason=p.Reason switch
        {
            SwitchEndReason.UnsupportedChannel=>"HOST",SwitchEndReason.NoLocalLink=>"LINK",SwitchEndReason.AlreadyCurrent=>"SAME",
            SwitchEndReason.ConnectionChanged=>"GEN",SwitchEndReason.LocalDeparture=>"LEFT",SwitchEndReason.TargetReported=>"RPT",
            SwitchEndReason.RetryLimit=>"LIMIT",SwitchEndReason.Superseded=>"NEW",SwitchEndReason.Deadline=>"TIME",SwitchEndReason.Exception=>"ERR",_=>"NA",
        };
        return $"{Prefix}|R{request:D4}|{source}{target+1}|{role}|{stage}|{verdict}|{outcome}|{reason}|E{elapsed}|D{p.ElapsedMs}|I{p.DurationMs}|Q{p.QueueMs}|WT{p.WriteMs}|QT{p.VerifyMs}|A{p.Attempt}|N{p.SendCalls}|RT{Math.Max(0,p.SendCalls-1)}|W{p.Writes}|H{(p.ReportedHost is {} h?h+1:0)}";
    }
}
