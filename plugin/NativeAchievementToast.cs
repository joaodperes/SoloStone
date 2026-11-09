using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using MiniJSON;
using Hearthstone.DataModels;
using Hearthstone.Progression;
using Hearthstone.UI;
using UnityEngine;

// Presentation only. Unlocks and rewards are already durable before this owns a widget.
internal static class NativeAchievementToast
{
    private static NativeAchievementStep step;
    private static ManualLogSource log;
    private static float retryAt;

    internal static bool Active => step?.Active == true;
    internal static string Diagnostics => step?.Diagnostics ?? "idle";
    internal static JsonNode State() => new JsonNode {
        { "active", Active }, { "visible", step?.Visible == true },
        { "visibleObserved", step?.VisibleObserved == true }, { "soundCalls", step?.SoundCalls ?? 0 },
        { "pendingMatchId", NativeUIProbe.RewardProfile?.PendingAchievementToastMatchId },
        { "diagnostics", Diagnostics }
    };

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        step = new NativeAchievementStep(logger);
        NativeResultsUI.AchievementStep = step;
        harmony.Patch(AccessTools.DeclaredMethod(typeof(AchievementToast), "Hide"),
            prefix: new HarmonyMethod(typeof(NativeAchievementToast), nameof(BeforeHide)));

        Type spellAudio = GameTypes.ByName("SpellStateAudioSource")
            ?? throw new TypeLoadException("SpellStateAudioSource");
        foreach (string method in new[] { "Play", "PlayNow" })
            harmony.Patch(AccessTools.DeclaredMethod(spellAudio, method),
                prefix: new HarmonyMethod(typeof(NativeAchievementToast), nameof(BeforeSpellAudio)),
                finalizer: new HarmonyMethod(typeof(NativeAchievementToast), nameof(AfterSpellAudio)));
        // By name: a metadata token is only right on the build it was read from, and a retail patch moves it silently.
        foreach (MethodInfo method in new[] {
            AccessTools.Method(typeof(SoundManager), "Play", new[] { GameTypes.ByName("UnityEngine.AudioSource"), typeof(SoundDef), GameTypes.ByName("UnityEngine.AudioClip"), typeof(SoundManager.SoundOptions) }),
            AccessTools.Method(typeof(SoundManager), "PlayClip", new[] { typeof(SoundPlayClipArgs), typeof(bool), typeof(SoundManager.SoundOptions) })
        })
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(NativeAchievementToast), nameof(ObserveSoundCall)));
        try
        {
            Type stateSound = GameTypes.ByName("Hearthstone.UI.PlaySoundClipStateAction")
                ?? throw new TypeLoadException("Hearthstone.UI.PlaySoundClipStateAction");
            foreach (MethodBase method in new[] {
                AccessTools.DeclaredMethod(stateSound, "Run", new[] { typeof(bool) }),
                AccessTools.DeclaredMethod(stateSound, "HandleReady", new[] { typeof(object) })
            })
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(NativeAchievementToast), nameof(BeforeStateSound)),
                    finalizer: new HarmonyMethod(typeof(NativeAchievementToast), nameof(AfterStateSound)));
            MethodInfo loadAndPlay = typeof(SoundManager).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Single(method => method.Name == "LoadAndPlayClip" && method.GetParameters().Length == 2);
            harmony.Patch(loadAndPlay,
                prefix: new HarmonyMethod(typeof(NativeAchievementToast), nameof(ObserveSoundCall)));
        }
        catch (Exception error)
        {
            log?.LogWarning("NATIVE_ACHIEVEMENT_SOUND_DIAGNOSTIC disabled=" + error.Message);
        }
    }

    internal static void Update()
    {
        if (step == null) return;
        if (step.Active && !step.ContextReady)
        {
            step.CancelForContext();
            retryAt = Time.realtimeSinceStartup + 2f;
            return;
        }
        step.Update();
        if (step.Active || Time.realtimeSinceStartup < retryAt) return;
        LocalProfile profile = NativeUIProbe.RewardProfile;
        if (profile?.PendingAchievementToastMatchId != null && NativeUIProbe.AchievementToastContextReady
            && profile.PendingAchievementToast is LocalMatchRecord pending)
        {
            step.PresentDeferred(profile, pending, () => { }, () => retryAt = Time.realtimeSinceStartup + 2f);
            return;
        }
        string expectedId = profile?.PendingNonMatchAchievementNoticeId;
        if (expectedId == null) return;
        if (!NativeUIProbe.AchievementToastContextReady) return;
        if (expectedId != "the-complete-works")
        {
            log?.LogWarning("NATIVE_ACHIEVEMENT_NOTICE unsupported=" + expectedId);
            retryAt = Time.realtimeSinceStartup + 5f;
            return;
        }
        LocalAchievementGrantReceipt[] grants = profile.Achievements.Grants
            .Where(grant => grant.AchievementId == expectedId).ToArray();
        if (grants.Length != 1)
        {
            log?.LogWarning("NATIVE_ACHIEVEMENT_NOTICE grant-count=" + grants.Length + ";id=" + expectedId);
            retryAt = Time.realtimeSinceStartup + 5f;
            return;
        }
        step.PresentNotice(profile, expectedId, grants[0], () => {
            try
            {
                if (!ReferenceEquals(profile, NativeUIProbe.RewardProfile))
                {
                    retryAt = Time.realtimeSinceStartup + 2f;
                    log?.LogInfo("NATIVE_ACHIEVEMENT_NOTICE_ACK retired-profile id=" + expectedId);
                    return;
                }
                bool acknowledged = profile.AcknowledgeNonMatchAchievementNotice(expectedId);
                log?.LogInfo("NATIVE_ACHIEVEMENT_NOTICE_ACK id=" + expectedId + ";acknowledged=" + acknowledged);
            }
            catch (Exception error)
            {
                retryAt = Time.realtimeSinceStartup + 2f;
                log?.LogWarning("NATIVE_ACHIEVEMENT_NOTICE_ACK id=" + expectedId + ";error=" + error);
            }
        }, () => retryAt = Time.realtimeSinceStartup + 2f);
    }

    private static void BeforeHide(AchievementToast __instance) => step?.ObserveNormalHide(__instance);

    private static void BeforeSpellAudio(object __instance, out bool __state)
    {
        __state = step?.BeginOwnedSpell(__instance) == true;
    }

    private static Exception AfterSpellAudio(Exception __exception, bool __state)
    {
        if (__state) step?.EndOwnedSpell();
        return __exception;
    }

    private static void ObserveSoundCall(MethodBase __originalMethod, object[] __args)
    {
        try { step?.ObserveSound(__originalMethod, __args); }
        catch (Exception error) { log?.LogWarning("NATIVE_ACHIEVEMENT_SOUND_DIAGNOSTIC " + error.Message); }
    }

    private static void BeforeStateSound(object __instance, MethodBase __originalMethod, out bool __state)
    {
        try { __state = step?.BeginOwnedStateSound(__instance, __originalMethod) == true; }
        catch (Exception error)
        {
            __state = false;
            log?.LogWarning("NATIVE_ACHIEVEMENT_SOUND_DIAGNOSTIC " + error.Message);
        }
    }

    private static Exception AfterStateSound(Exception __exception, bool __state)
    {
        if (__state) step?.EndOwnedStateSound();
        return __exception;
    }
}

internal sealed class NativeAchievementStep : ILocalAchievementStep
{
    private const string ToastPrefab = "AchievementToast.prefab:9fa72c338c657d54fb9f63de21c1e4f8";
    private const float AttemptTimeout = 45f;
    private readonly ManualLogSource log;
    private readonly LocalAchievementToastLifetime lifetime = new LocalAchievementToastLifetime();
    private readonly HashSet<object> ownedAudioSources = new HashSet<object>();
    private readonly HashSet<object> ownedSpellAudio = new HashSet<object>();
    private readonly HashSet<VisualController> ownedVisualControllers = new HashSet<VisualController>();
    private static readonly Type AudioSourceType = Type.GetType("UnityEngine.AudioSource, UnityEngine.AudioModule", false);
    private static readonly FieldInfo StateActionField = AccessTools.Field(typeof(StateActionImplementation), "<StateAction>k__BackingField");
    private static readonly FieldInfo CaptionField = AccessTools.Field(typeof(AchievementToast), "m_text");
    private static readonly PropertyInfo CaptionRenderObject = CaptionField == null ? null : AccessTools.Property(CaptionField.FieldType, "RenderOnObject");
    private WidgetInstance widget;
    private AchievementToast toast;
    private Component caption;
    private Renderer captionRenderer;
    private int renderedFrame = -2;
    private bool shown, captionInputsLogged, rendererDiagnosticFailed;
    private float captionReadyAt = -1f;
    private Action<object> ready;
    private Action loadFailed;
    private Action completed, failed;
    private LocalProfile ownerProfile;
    private string ownerMatch;
    private bool notice;
    private string operation = "idle", components = "none", sound = "none";
    private float queuedAt, readyAt, shownAt, hiddenAt;
    private int token, attributedSounds, unattributedSounds, spellDepth, stateSoundDepth;
    private int stateSoundRuns, stateSoundReady, serializedSoundActions;
    private string stateSoundCause = "none";

    internal NativeAchievementStep(ManualLogSource logger) { log = logger; }
    internal bool Active => lifetime.Active;
    internal bool ActiveNotice => Active && notice;
    internal bool Visible
    {
        get { return CaptionReady(); }
    }
    internal bool VisibleObserved { get; private set; }
    internal int SoundCalls => attributedSounds;
    internal bool ContextReady => ActiveNotice
        ? ReferenceEquals(ownerProfile, NativeUIProbe.RewardProfile) && NativeUIProbe.AchievementToastContextReady
        : Active && NativeUIProbe.MatchAchievementToastContextReady(ownerProfile, ownerMatch);
    internal string Diagnostics => "operation=" + operation + ";active=" + Active + ";generation=" + lifetime.Generation
        + ";visible=" + Visible + ";visibleObserved=" + VisibleObserved
        + ";queued=" + F(queuedAt) + ";ready=" + F(readyAt) + ";shown=" + F(shownAt) + ";hide=" + F(hiddenAt)
        + ";components=" + components + ";attributedSounds=" + attributedSounds + ";sound=" + sound
        + ";unattributedSounds=" + unattributedSounds + ";serializedSoundActions=" + serializedSoundActions
        + ";stateSoundRuns=" + stateSoundRuns + ";stateSoundReady=" + stateSoundReady + ";laneMuted=" + LaneMute.Active;

    public void Present(LocalResultsReceipt receipt, Action done, Action failure)
    {
        if (receipt == null) throw new ArgumentNullException(nameof(receipt));
        LocalProfile profile = NativeUIProbe.RewardProfile;
        if (profile != null && !profile.HasPendingAchievementToast(receipt.MatchId)) { done(); return; }
        Start("match:" + receipt.MatchId, receipt.AchievementGrants, done, failure,
            profile, receipt.MatchId, false);
    }

    internal void PresentDeferred(LocalProfile profile, LocalMatchRecord match, Action done, Action failure) =>
        Start("deferred:" + match.Id, match.AchievementGrants, done, failure, profile, match.Id, true);

    internal void PresentNotice(LocalProfile profile, string id, LocalAchievementGrantReceipt grant, Action done, Action failure) =>
        Start("notice:" + id, new[] { grant }, done, failure,
            profile ?? throw new ArgumentNullException(nameof(profile)), null, true);

    private void Start(string id, IEnumerable<LocalAchievementGrantReceipt> grants, Action done, Action failure,
        LocalProfile owner, string match, bool isNotice)
    {
        if (done == null || failure == null) throw new ArgumentNullException(done == null ? nameof(done) : nameof(failure));
        if (Active) Retire();
        var presentation = LocalAchievementPresentation.Build(grants);
        if (presentation.Count == 0) { done(); return; }
        operation = id; completed = done; failed = failure;
        ownerProfile = owner ?? throw new InvalidOperationException("Achievement toast profile missing.");
        ownerMatch = match; notice = isNotice;
        attributedSounds = unattributedSounds = spellDepth = stateSoundDepth = stateSoundRuns = stateSoundReady = serializedSoundActions = 0;
        sound = stateSoundCause = "none"; components = "none";
        VisibleObserved = false;
        shown = captionInputsLogged = rendererDiagnosticFailed = false; renderedFrame = -2; captionReadyAt = -1f;
        queuedAt = Time.realtimeSinceStartup; readyAt = shownAt = hiddenAt = 0;
        token = lifetime.Begin();
        try
        {
            WidgetInstance expected = widget = WidgetInstance.Create(ToastPrefab);
            if (expected == null) throw new InvalidOperationException("Achievement toast widget missing.");
            int expectedToken = token;
            loadFailed = () => Fail(expectedToken, expected, new InvalidOperationException("Achievement toast widget load failed."));
            expected.WidgetLoadFailed += loadFailed;
            ready = _ => Ready(expectedToken, expected, presentation);
            expected.RegisterReadyListenerDoOnce(ready, expected, true);
            log?.LogInfo("NATIVE_ACHIEVEMENT_QUEUED " + operation + ";caption=" + presentation.Caption);
        }
        catch (Exception error) { Fail(token, widget, error); }
    }

    private void Ready(int expectedToken, WidgetInstance expected, LocalAchievementPresentation presentation)
    {
        if (!IsCurrent(expectedToken, expected)) { DestroyStale(expected); return; }
        try
        {
            Exception unsafeContext = ContextFailure("ready");
            if (unsafeContext != null) { Fail(expectedToken, expected, unsafeContext); return; }
            DetachLoad(expected);
            readyAt = Time.realtimeSinceStartup;
            toast = expected.GetComponentInChildren<AchievementToast>(true)
                ?? throw new InvalidOperationException("Achievement toast component missing.");
            caption = CaptionField?.GetValue(toast) as Component
                ?? throw new InvalidOperationException("Achievement toast caption missing.");
            var sentinel = toast.gameObject.AddComponent<NativeAchievementToastSentinel>();
            sentinel.Owner = this; sentinel.Token = expectedToken; sentinel.Expected = toast;
            InspectOwnedSubtree(toast.gameObject);
            toast.Initialize(new AchievementDataModel {
                Name = presentation.Caption, Tier = 1, NextTierID = 0,
                Status = (AchievementManager.AchievementStatus)2,
                Progress = 1, Quota = 1,
                DisplayMode = (AchievementManager.AchievementDisplayMode)0,
                IsLocked = false
            });
            toast.Show();
            shown = true;
            shownAt = Time.realtimeSinceStartup;
            ObserveCaptionHold();
            BindCaptionRenderer();
            log?.LogInfo("NATIVE_ACHIEVEMENT_SHOWN " + operation + ";components=" + components + ";laneMuted=" + LaneMute.Active);
        }
        catch (Exception error) { Fail(expectedToken, expected, error); }
    }

    internal void ObserveNormalHide(AchievementToast value)
    {
        if (!Active || !ReferenceEquals(value, toast)) return;
        Exception unsafeContext = ContextFailure("hide");
        if (unsafeContext != null) { FailWithoutHiding(unsafeContext); return; }
        ObserveCaptionHold();
        if (!lifetime.ObserveNormalHide(token, true)) return;
        shown = false;
        hiddenAt = Time.realtimeSinceStartup;
        log?.LogInfo("NATIVE_ACHIEVEMENT_HIDE " + operation);
    }

    internal void Destroyed(int expectedToken, AchievementToast expected)
    {
        bool current = lifetime.Active && expectedToken == lifetime.Generation && ReferenceEquals(expected, toast);
        if (!current) return;
        Exception unsafeContext = ContextFailure("destroy");
        if (unsafeContext != null)
        {
            FailWithoutHiding(unsafeContext);
            return;
        }
        bool success = lifetime.Destroyed(expectedToken, true, VisibleObserved);
        if (success && ownerMatch != null)
        {
            try
            {
                if (!ReferenceEquals(ownerProfile, NativeUIProbe.RewardProfile))
                    throw new InvalidOperationException("Achievement toast profile changed before acknowledgement.");
                ownerProfile.AcknowledgeAchievementToast(ownerMatch);
                log?.LogInfo("NATIVE_ACHIEVEMENT_ACK match=" + ownerMatch);
            }
            catch (Exception error)
            {
                success = false;
                log?.LogWarning("NATIVE_ACHIEVEMENT_ACK_RETRY match=" + ownerMatch + ";error=" + error);
            }
        }
        Action callback = success ? completed : failed;
        string result = success ? "complete" : "unconfirmed-or-unsaved";
        LogSoundSummary("destroy");
        CleanupDestroyed();
        log?.LogInfo("NATIVE_ACHIEVEMENT_DESTROYED " + operation + ";result=" + result);
        callback?.Invoke();
    }

    internal void Update()
    {
        if (!Active) return;
        ObserveCaptionHold();
        BindCaptionRenderer();
        if (Time.realtimeSinceStartup - queuedAt > AttemptTimeout)
            Fail(token, widget, new TimeoutException("Achievement toast attempt timed out."));
        else if (widget == null)
            Fail(token, widget, new InvalidOperationException("Achievement toast widget was destroyed outside its normal hide."));
    }

    private bool CaptionReady()
    {
        object text = ReadProperty(caption, "Text");
        if (shown && !captionInputsLogged)
        {
            captionInputsLogged = true;
            object alpha = ReadProperty(caption, "TextAlpha"), hidden = Read(caption, "m_Hidden");
            log?.LogInfo("NATIVE_ACHIEVEMENT_CAPTION_INPUTS " + operation + ";active=" + Active + ";shown=" + shown
                + ";toastActive=" + (toast != null ? toast.gameObject.activeInHierarchy.ToString() : "null")
                + ";captionActive=" + (caption != null ? caption.gameObject.activeInHierarchy.ToString() : "null")
                + ";Text=" + (text == null ? "null" : string.IsNullOrWhiteSpace(text as string) ? "empty" : "nonempty")
                + ";TextAlpha=" + (alpha ?? "null") + ";m_Hidden=" + (hidden ?? "null"));
        }
        return Active && shown && toast != null && toast.gameObject.activeInHierarchy
            && caption != null && caption.gameObject.activeInHierarchy
            && !string.IsNullOrWhiteSpace(text as string);
    }

    private void ObserveCaptionHold()
    {
        if (!CaptionReady()) { captionReadyAt = -1f; return; }
        if (captionReadyAt < 0) captionReadyAt = Time.realtimeSinceStartup;
        if (VisibleObserved || Time.realtimeSinceStartup - captionReadyAt < 0.5f) return;
        VisibleObserved = true;
        log?.LogInfo("NATIVE_ACHIEVEMENT_VISIBLE " + operation + ";heldSeconds="
            + F(Time.realtimeSinceStartup - captionReadyAt));
    }

    private Renderer CaptionRenderer()
    {
        if (caption == null) return null;
        if (ReadProperty(caption, "RenderToTexture") is bool baked && baked)
        {
            var target = CaptionRenderObject?.GetValue(caption, null) as GameObject;
            return target != null ? target.GetComponent<Renderer>()
                : Read(Read(caption, "m_UberTextRenderToTexture"), "m_planeMeshRenderer") as Renderer;
        }
        // UberText renders through a generated child, not the object holding the UberText component.
        object rendering = Read(caption, "m_UberTextRendering");
        return rendering?.GetType().GetMethod("GetTextMeshRenderer")?.Invoke(rendering, null) as Renderer;
    }

    internal void BindCaptionRenderer()
    {
        if (!Active || !shown) return;
        if (rendererDiagnosticFailed) return;
        try
        {
            Renderer renderer = CaptionRenderer();
            if (renderer == null || ReferenceEquals(renderer, captionRenderer)) return;
            captionRenderer = renderer;
            var observer = renderer.gameObject.AddComponent<NativeAchievementToastRenderObserver>();
            observer.Owner = this; observer.Token = token; observer.Expected = toast; observer.Renderer = renderer;
            log?.LogInfo("NATIVE_ACHIEVEMENT_CAPTION " + operation + ";path=" + Path(renderer.transform)
                + ";layer=" + renderer.gameObject.layer + ";baked=" + ReadProperty(caption, "RenderToTexture"));
        }
        catch (Exception error)
        {
            rendererDiagnosticFailed = true;
            log?.LogWarning("NATIVE_ACHIEVEMENT_RENDER_DIAGNOSTIC " + operation + ";error=" + error.Message);
        }
    }

    internal void ObserveRendered(int expectedToken, AchievementToast expected, Renderer renderer, Camera camera)
    {
        if (expectedToken != lifetime.Generation || !ReferenceEquals(expected, toast) || !CaptionReady()
            || renderer == null || !ReferenceEquals(renderer, captionRenderer) || !renderer.enabled
            || !renderer.gameObject.activeInHierarchy || camera == null || !camera.isActiveAndEnabled
            || camera.targetTexture != null || (camera.cullingMask & 1 << renderer.gameObject.layer) == 0) return;
        // OnWillRenderObject is camera-scoped evidence; isVisible also includes texture/editor cameras and stale frames.
        if (renderedFrame >= 0) return;
        renderedFrame = Time.frameCount;
        log?.LogInfo("NATIVE_ACHIEVEMENT_RENDERED " + operation + ";camera=" + camera.name
            + ";layer=" + renderer.gameObject.layer + ";frame=" + renderedFrame);
    }

    internal void CancelForContext()
    {
        if (!Active) return;
        Fail(token, widget, new InvalidOperationException("Achievement toast retired because its safe presentation context ended."));
    }

    private Exception ContextFailure(string boundary)
    {
        try
        {
            return ContextReady ? null : new InvalidOperationException(
                "Achievement toast lost its safe presentation context at " + boundary + ".");
        }
        catch (Exception error)
        {
            return new InvalidOperationException("Achievement toast context check failed at " + boundary + ".", error);
        }
    }

    // Hide/OnDestroy already own native cleanup at these callback boundaries; never re-enter Hide.
    private void FailWithoutHiding(Exception error)
    {
        Action callback = failed;
        lifetime.Retire();
        LogSoundSummary("unsafe-context");
        CleanupDestroyed();
        log?.LogWarning("NATIVE_ACHIEVEMENT_FALLBACK " + operation + ";" + error);
        callback?.Invoke();
    }

    public void Retire()
    {
        if (!Active) return;
        AchievementToast ownedToast = toast;
        WidgetInstance ownedWidget = widget;
        lifetime.Retire();
        LogSoundSummary("retire");
        CleanupReferences(ownedWidget);
        try
        {
            if (ownedToast != null) ownedToast.Hide();
            else if (ownedWidget != null) UnityEngine.Object.Destroy(ownedWidget.gameObject);
        }
        catch (Exception error) { log?.LogWarning("NATIVE_ACHIEVEMENT_RETIRE " + error.Message); }
    }

    private bool IsCurrent(int expectedToken, WidgetInstance expected) => lifetime.Active
        && expectedToken == lifetime.Generation && ReferenceEquals(expected, widget);

    private void Fail(int expectedToken, WidgetInstance expected, Exception error)
    {
        if (!IsCurrent(expectedToken, expected)) { DestroyStale(expected); return; }
        Action callback = failed;
        AchievementToast ownedToast = toast;
        WidgetInstance ownedWidget = widget;
        lifetime.Retire();
        LogSoundSummary("failure");
        CleanupReferences(ownedWidget);
        try
        {
            if (ownedToast != null) ownedToast.Hide();
            else if (ownedWidget != null) UnityEngine.Object.Destroy(ownedWidget.gameObject);
        }
        catch (Exception cleanup) { log?.LogWarning("NATIVE_ACHIEVEMENT_CLEANUP " + cleanup.Message); }
        log?.LogWarning("NATIVE_ACHIEVEMENT_FALLBACK " + operation + ";" + error);
        callback?.Invoke();
    }

    private void CleanupDestroyed()
    {
        WidgetInstance ownedWidget = widget;
        CleanupReferences(ownedWidget);
    }

    private void CleanupReferences(WidgetInstance ownedWidget)
    {
        DetachLoad(ownedWidget);
        try { if (ownedWidget != null && ready != null) ownedWidget.RemoveReadyListener(ready); } catch { }
        widget = null; toast = null; caption = null; captionRenderer = null; shown = false; ready = null; loadFailed = null;
        completed = failed = null;
        ownerProfile = null; ownerMatch = null; notice = false;
        ownedAudioSources.Clear(); ownedSpellAudio.Clear(); ownedVisualControllers.Clear();
        spellDepth = stateSoundDepth = 0; stateSoundCause = "none";
    }

    private void DetachLoad(WidgetInstance expected)
    {
        try { if (expected != null && loadFailed != null) expected.WidgetLoadFailed -= loadFailed; } catch { }
    }

    private static void DestroyStale(WidgetInstance expected)
    {
        try { if (expected != null) UnityEngine.Object.Destroy(expected.gameObject); } catch { }
    }

    private void InspectOwnedSubtree(GameObject root)
    {
        Component[] all = root.GetComponentsInChildren<Component>(true);
        components = string.Join(",", all.Where(value => value != null).Select(value => value.GetType().Name)
            .Distinct().OrderBy(value => value).ToArray());
        if (AudioSourceType != null)
            foreach (object source in root.GetComponentsInChildren(AudioSourceType, true))
            {
                ownedAudioSources.Add(source);
                log?.LogInfo("NATIVE_ACHIEVEMENT_AUDIO_SOURCE " + AudioSourceFacts(source));
            }
        foreach (Component component in all.Where(value => value != null && value.GetType().Name == "Spell"))
        {
            object map = AccessTools.Field(component.GetType(), "m_spellStateMap")?.GetValue(component);
            foreach (object state in Values(map))
            {
                object list = AccessTools.Field(state.GetType(), "m_AudioSources")?.GetValue(state);
                foreach (object spellSource in Items(list))
                {
                    if (spellSource == null) continue;
                    ownedSpellAudio.Add(spellSource);
                    object source = AccessTools.Field(spellSource.GetType(), "m_AudioSource")?.GetValue(spellSource);
                    if (source != null) ownedAudioSources.Add(source);
                    log?.LogInfo("NATIVE_ACHIEVEMENT_SPELL_AUDIO type=" + spellSource.GetType().Name
                        + ";enabled=" + Read(spellSource, "m_Enabled") + ";delay=" + Read(spellSource, "m_StartDelaySec")
                        + ";global=" + Read(spellSource, "m_PlayGlobally") + ";stop=" + Read(spellSource, "m_StopOnStateChange")
                        + ";comment=" + Read(spellSource, "m_Comment") + ";source=" + AudioSourceFacts(source));
                }
            }
        }
        foreach (VisualController controller in root.GetComponentsInChildren<VisualController>(true))
            try { InspectVisualController(controller); }
            catch (Exception error) { log?.LogWarning("NATIVE_ACHIEVEMENT_VISUAL_DIAGNOSTIC " + error.Message); }
        foreach (Component component in all.Where(value => value != null && value.GetType().Name == "PlayMakerFSM"))
            try { InspectPlayMaker(component); }
            catch (Exception error) { log?.LogWarning("NATIVE_ACHIEVEMENT_PLAYMAKER_DIAGNOSTIC " + error.Message); }
    }

    internal bool BeginOwnedSpell(object value)
    {
        if (!Active || !ownedSpellAudio.Contains(value)) return false;
        spellDepth++;
        return true;
    }

    internal void EndOwnedSpell() { if (spellDepth > 0) spellDepth--; }

    internal bool BeginOwnedStateSound(object implementation, MethodBase method)
    {
        if (!Active || implementation == null) return false;
        var action = StateActionField?.GetValue(implementation) as StateAction;
        var controller = action?.DataContext as VisualController;
        if (controller == null || !ownedVisualControllers.Contains(controller)) return false;
        string cause = Path(controller.transform) + ":" + StateActionFacts(action);
        log?.LogInfo("NATIVE_ACHIEVEMENT_STATE_SOUND operation=" + operation + ";method=" + method.Name
            + ";cause=" + cause + ";time=" + F(Time.realtimeSinceStartup));
        stateSoundCause = cause;
        stateSoundDepth++;
        if (method.Name == "Run") stateSoundRuns++;
        else stateSoundReady++;
        return true;
    }

    internal void EndOwnedStateSound()
    {
        if (stateSoundDepth > 0) stateSoundDepth--;
        if (stateSoundDepth == 0) stateSoundCause = "none";
    }

    internal void ObserveSound(MethodBase method, object[] args)
    {
        if (!Active) return;
        object source = args?.FirstOrDefault(value => value != null && AudioSourceType != null && AudioSourceType.IsInstanceOfType(value));
        bool attributed = source != null && ownedAudioSources.Contains(source) || spellDepth > 0 || stateSoundDepth > 0;
        if (!attributed) { unattributedSounds++; return; }
        attributedSounds++;
        string cause = source != null ? AudioSourceFacts(source)
            : stateSoundDepth > 0 ? "owned-state-action:" + stateSoundCause : "owned-spell-causal";
        sound = method.Name + ":" + cause;
        log?.LogInfo("NATIVE_ACHIEVEMENT_SOUND operation=" + operation + ";time=" + F(Time.realtimeSinceStartup)
            + ";method=" + method.Name + ";source=" + cause
            + ";args=" + string.Join(",", (args ?? Array.Empty<object>()).Select(SoundArgument).ToArray())
            + ";laneMuted=" + LaneMute.Active);
    }

    private void InspectVisualController(VisualController controller)
    {
        if (controller == null) return;
        ownedVisualControllers.Add(controller);
        var collection = Read(controller, "m_stateCollection") as WidgetBehaviorStateCollection;
        log?.LogInfo("NATIVE_ACHIEVEMENT_VISUAL path=" + Path(controller.transform) + ";label=" + controller.Label
            + ";state=" + controller.State + ";requested=" + controller.RequestedState);
        if (collection?.States == null) return;
        foreach (WidgetBehaviorState state in collection.States.Where(value => value != null))
        {
            StateAction[] actions = state.Actions?.Where(value => value != null).ToArray() ?? Array.Empty<StateAction>();
            log?.LogInfo("NATIVE_ACHIEVEMENT_VISUAL_STATE path=" + Path(controller.transform) + ";state=" + state.Name
                + ";actions=" + string.Join(",", actions.Select(value => value.Type.ToString()).ToArray()));
            foreach (StateAction action in actions)
            {
                if (action.Type == StateAction.ActionType.PlaySoundClip)
                {
                    serializedSoundActions++;
                    log?.LogInfo("NATIVE_ACHIEVEMENT_VISUAL_SOUND path=" + Path(controller.transform)
                        + ";state=" + state.Name + ";" + StateActionFacts(action));
                }
                else if (action.Type == StateAction.ActionType.SendPlayMakerEvent)
                    log?.LogInfo("NATIVE_ACHIEVEMENT_VISUAL_EVENT path=" + Path(controller.transform)
                        + ";state=" + state.Name + ";event=" + action.GetString(0) + ";target=" + TargetPath(action));
            }
        }
    }

    private void InspectPlayMaker(Component component)
    {
        object fsm = ReadProperty(component, "Fsm");
        object states = ReadProperty(fsm, "States") ?? Read(fsm, "States");
        object events = ReadProperty(fsm, "Events") ?? Read(fsm, "Events");
        log?.LogInfo("NATIVE_ACHIEVEMENT_PLAYMAKER path=" + Path(component.transform)
            + ";name=" + SerializedValue(ReadProperty(component, "FsmName") ?? ReadProperty(fsm, "Name"))
            + ";state=" + SerializedValue(ReadProperty(component, "ActiveStateName") ?? ReadProperty(fsm, "ActiveStateName"))
            + ";events=" + string.Join(",", Items(events).Select(SerializedValue).ToArray()));
        foreach (object state in Items(states))
        {
            object[] actions = Items(ReadProperty(state, "Actions") ?? Read(state, "Actions")).ToArray();
            log?.LogInfo("NATIVE_ACHIEVEMENT_PLAYMAKER_STATE path=" + Path(component.transform)
                + ";state=" + SerializedValue(ReadProperty(state, "Name") ?? Read(state, "Name"))
                + ";actions=" + string.Join(",", actions.Where(value => value != null)
                    .Select(value => value.GetType().FullName).ToArray()));
            foreach (object action in actions.Where(value => value != null))
            {
                string fields = SerializedAudioFields(action);
                if (fields.Length > 0)
                    log?.LogInfo("NATIVE_ACHIEVEMENT_PLAYMAKER_AUDIO path=" + Path(component.transform)
                        + ";action=" + action.GetType().FullName + ";" + fields);
            }
        }
    }

    private void LogSoundSummary(string boundary) => log?.LogInfo("NATIVE_ACHIEVEMENT_SOUND_SUMMARY operation=" + operation
        + ";boundary=" + boundary + ";serializedSoundActions=" + serializedSoundActions + ";stateSoundRuns=" + stateSoundRuns
        + ";stateSoundReady=" + stateSoundReady + ";attributedSounds=" + attributedSounds
        + ";unattributedSounds=" + unattributedSounds + ";sound=" + sound
        + ";laneMuted=" + LaneMute.Active);

    private static string StateActionFacts(StateAction action)
    {
        action.TryGetAssetAtIndex(0, out WeakAssetReference asset);
        action.TryGetIntValueAtIndex(0, out int category);
        action.TryGetFloatValueAtIndex(0, out float volume);
        action.TryGetFloatValueAtIndex(1, out float pitch);
        action.TryGetFloatValueAtIndex(2, out float spatial);
        action.TryGetFloatValueAtIndex(3, out float delay);
        return "asset=" + asset.AssetString + ";category=" + category + ";volume=" + F(volume)
            + ";pitch=" + F(pitch) + ";spatial=" + F(spatial) + ";delay=" + F(delay)
            + ";target=" + TargetPath(action);
    }

    private static string TargetPath(StateAction action)
    {
        try { return Path(action?.TargetGameObject?.transform); }
        catch (Exception error) { return "unresolved:" + error.GetType().Name; }
    }

    private static string SerializedAudioFields(object action)
    {
        var facts = action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => new[] { "audio", "clip", "sound", "event", "prefab" }
                .Any(term => field.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0))
            .Select(field => field.Name + "=" + SerializedValue(field.GetValue(action))).ToArray();
        return string.Join(";", facts);
    }

    private static string SerializedValue(object value)
    {
        if (value == null) return "null";
        if (value is string || value.GetType().IsPrimitive || value.GetType().IsEnum) return value.ToString();
        if (value is UnityEngine.Object unity) return unity.name ?? value.GetType().Name;
        object nested = ReadProperty(value, "Value");
        if (nested != null && !ReferenceEquals(nested, value)) return SerializedValue(nested);
        object name = ReadProperty(value, "Name");
        return name == null ? value.ToString() : name.ToString();
    }

    private static string SoundArgument(object value)
    {
        object asset = ReadProperty(value, "AssetString");
        return asset == null ? Identity(value) : value.GetType().Name + ":" + asset;
    }

    private static IEnumerable<object> Values(object map)
    {
        foreach (object entry in Items(map))
        {
            object value = entry?.GetType().GetProperty("Value")?.GetValue(entry, null);
            foreach (object state in Items(value)) yield return state;
        }
    }

    private static IEnumerable<object> Items(object value)
    {
        if (value is IEnumerable items) foreach (object item in items) yield return item;
    }

    private static string AudioSourceFacts(object source)
    {
        if (source == null) return "none";
        var component = source as Component;
        object clip = source.GetType().GetProperty("clip")?.GetValue(source, null);
        object mixer = source.GetType().GetProperty("outputAudioMixerGroup")?.GetValue(source, null);
        return "path=" + Path(component?.transform) + ":clip=" + Identity(clip)
            + ":playOnAwake=" + ReadProperty(source, "playOnAwake") + ":enabled=" + ReadProperty(source, "enabled")
            + ":active=" + component?.gameObject.activeInHierarchy + ":volume=" + ReadProperty(source, "volume")
            + ":pitch=" + ReadProperty(source, "pitch") + ":mixer=" + Identity(mixer);
    }

    private static string Path(Transform node)
    {
        var names = new List<string>();
        for (Transform current = node; current != null; current = current.parent) names.Add(current.name);
        names.Reverse(); return string.Join("/", names.ToArray());
    }

    private static object Read(object value, string field) => value == null ? null : AccessTools.Field(value.GetType(), field)?.GetValue(value);
    private static object ReadProperty(object value, string property) => value == null ? null : value.GetType().GetProperty(property)?.GetValue(value, null);
    private static string Identity(object value)
    {
        if (value == null) return "null";
        if (value is UnityEngine.Object unity) return unity.name ?? value.GetType().Name;
        return value.ToString();
    }
    private static string F(float value) => value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class NativeAchievementToastSentinel : MonoBehaviour
{
    internal NativeAchievementStep Owner;
    internal AchievementToast Expected;
    internal int Token;
    private void LateUpdate() => Owner?.BindCaptionRenderer();
    private void OnDestroy() => Owner?.Destroyed(Token, Expected);
}

internal sealed class NativeAchievementToastRenderObserver : MonoBehaviour
{
    internal NativeAchievementStep Owner;
    internal AchievementToast Expected;
    internal Renderer Renderer;
    internal int Token;
    private void OnWillRenderObject() => Owner?.ObserveRendered(Token, Expected, Renderer, Camera.current);
}
