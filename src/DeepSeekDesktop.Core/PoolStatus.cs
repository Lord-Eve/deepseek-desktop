using System.Text.Json;

namespace DeepSeekDesktop.Core;

public sealed record PoolStatus(int Idle, int Busy, int Error, int Invalid)
{
    public bool HasUsableAccount => Idle > 0 || Busy > 0;
    public bool HasFailures => Error > 0 || Invalid > 0;
    public string Title => !HasUsableAccount ? "无可用账号" : HasFailures ? "部分账号异常" : "账号已就绪";
    public string Detail => $"账号池：空闲 {Idle} · 忙碌 {Busy} · 错误 {Error} · 不可用 {Invalid}。" +
        (!HasUsableAccount ? "本地端口已启动，但当前无法提供对话；请查看登录错误。" :
        "本地端口已启动；真实对话请单独测试。");

    public static PoolStatus Parse(JsonElement value)
    {
        int Read(string name) => value.GetProperty(name).TryGetInt32(out var count) && count >= 0
            ? count : throw new FormatException("账号池状态无效。");
        return new(Read("idle"), Read("busy"), Read("error"), Read("invalid"));
    }
}
