using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using InteropGenerator.Runtime;
using OmenTools.Dalamud;
using OmenTools.Extensions;

namespace OccultPot.Core.Dig;

internal sealed unsafe class PotTalkHooks : IDisposable
{
    private delegate void ShowBattleTalkDelegate(
        UIModule* module, CStringPointer name, CStringPointer text, float duration, byte style);

    private delegate void ShowBattleTalkImageDelegate(
        UIModule* module,
        CStringPointer name,
        CStringPointer text,
        float duration,
        uint image,
        byte style,
        int sound,
        uint entityID);

    private readonly Action<string> onText;
    private Hook<ShowBattleTalkDelegate>? talk;
    private Hook<ShowBattleTalkImageDelegate>? talkImage;
    private bool disposed;

    internal PotTalkHooks(Action<string> onText) => this.onText = onText;

    internal void Enable()
    {
        if ((talk != null && talkImage != null) || disposed)
            return;

        var ui = UIModule.Instance();
        if (ui == null)
            return;

        try
        {
            talk ??= ui->VirtualTable->HookVFuncFromName(
                "ShowBattleTalk", (ShowBattleTalkDelegate)OnTalk);
            talk.Enable();

            talkImage ??= ui->VirtualTable->HookVFuncFromName(
                "ShowBattleTalkImage", (ShowBattleTalkImageDelegate)OnTalkImage);
            talkImage.Enable();
        }
        catch (Exception ex)
        {
            // 部分客户端签名或 vfunc 名对不上时仍可用聊天与 Toast
            DLog.Error("[OccultPot] BattleTalk Hook 启用失败", ex);
        }
    }

    private void OnTalk(UIModule* module, CStringPointer name, CStringPointer text, float duration, byte style)
    {
        talk!.Original(module, name, text, duration, style);
        Forward(text);
    }

    private void OnTalkImage(
        UIModule* module,
        CStringPointer name,
        CStringPointer text,
        float duration,
        uint image,
        byte style,
        int sound,
        uint entityID)
    {
        talkImage!.Original(module, name, text, duration, image, style, sound, entityID);
        Forward(text);
    }

    private void Forward(CStringPointer text)
    {
        if (!text.HasValue)
            return;

        var line = text.ExtractText();
        if (!string.IsNullOrEmpty(line))
            onText(line);
    }

    public void Dispose()
    {
        disposed = true;
        talk?.Dispose();
        talk = null;
        talkImage?.Dispose();
        talkImage = null;
    }
}
