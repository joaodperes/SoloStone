using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blizzard.T5.Core.Utils;
using Blizzard.T5.Jobs;
using Hearthstone.Core;
using MiniJSON;
using UnityEngine;

// Native card pictures on the Deck Guide pages (owner 2026-11-05). A guide page that has card ids is raised as the game's own CardListPopup
// (the dialog that lists cards with their real actors, paging and hover) instead of the text-only AlertPopup. The page text rides in the
// popup's description; page one has NEXT and a cloned CLOSE button, the last page its one CLOSE, and Escape closes the chain. Every step fails closed
// back to the text AlertPopup (NativeRecipeGuide.ShowPages): an id the card database does not know, no dialog manager, an exception, or a
// popup that does not appear within Timeout seconds. The popup owns and destroys its card actors; nothing is created here but the watcher.
internal static class NativeGuideCards
{
    internal const float Timeout = 4f;
    // Test-control view of the page that is up (null otherwise): the same header and body strings the AlertPopup page shows.
    internal static Page Current;

    internal sealed class Page
    {
        internal string PopupId, Header, Text;
        internal string[] CardIds;
        internal bool Next, Appeared, Advance, Finished, Abandoned;
        internal CardListPopup.Info Info;
        internal UIBButton Close;
        internal float Started;
        internal Action<bool> Done;   // true: the player pressed NEXT; false: closed
        internal Action Failed;       // never appeared: raise the text page instead
        internal GameObject Watcher;
    }

    private static string reportsDir = "";

    // Test-only failure injection (live proof of the fallbacks): the test-control flag must be on and `guide-cards-fail.txt` beside it holds
    // one mode, consumed by the next page. id: the card database "does not know" an id; never: the popup is never requested; late: it is
    // requested after the timeout (the late popup must be ignored). Without the enable flag the file is never read.
    private static string ForcedMode()
    {
#if SOLOSTONE_INSTALLER && !SOLOSTONE_TEST_HOOKS
        return "";
#else
        try
        {
            string flag = Path.Combine(reportsDir, "guide-cards-fail.txt");
            if (reportsDir.Length == 0 || !File.Exists(Path.Combine(reportsDir, "enable-test-control")) || !File.Exists(flag)) return "";
            string mode = File.ReadAllText(flag).Trim();
            File.Delete(flag);
            return mode;
        }
        catch (Exception) { return ""; }
#endif
    }

    internal static bool TryShow(string popupId, string header, string text, IReadOnlyList<string> cardIds, bool next, Action<bool> done, Action failed)
    {
        try
        {
            if (Current != null || cardIds == null || cardIds.Count == 0 || cardIds.Count > RecipeLibrary.MaxGuideCards) return false;
            var dialogs = DialogManager.Get();
            if (dialogs == null) return false;
            string forced = ForcedMode();
            if (forced == "id") return false;
            var dbIds = new List<int>();
            foreach (string id in cardIds)
            {
                int dbId = GameUtils.TranslateCardIdToDbId(id);
                if (dbId <= 0) return false;
                dbIds.Add(dbId);
            }
            var page = new Page { PopupId = popupId, Header = header, Text = text, CardIds = cardIds.ToArray(), Next = next, Done = done, Failed = failed,
                Started = Time.unscaledTime };
            var info = new CardListPopup.Info {
                m_description = Description(header, text), m_cards = dbIds, m_changes = dbIds.ToDictionary(id => id, id => new List<CardChangeDbfRecord>()),
                m_useMultiLineDescription = true,
                m_callbackOnHide = (dialog, userData) => Finish(page, page.Advance)
            };
            page.Info = info;
            page.Watcher = new GameObject("SoloStoneGuideCards");
            page.Watcher.AddComponent<Watcher>().Page = page;
            Current = page;
            if (forced == "never") return true;
            if (forced == "late") Processor.ScheduleCallback(Timeout + 2f, false, _ => DialogManager.Get()?.ShowCardListPopup(UserAttentionBlocker.NONE, info));
            else dialogs.ShowCardListPopup(UserAttentionBlocker.NONE, info);
            return true;
        }
        catch (Exception error)
        {
            NativeUIProbe.LogRecipeFailure(new InvalidOperationException("guide-cards", error));
            Abandon();
            return false;
        }
    }

    // The panel reads m_changes[card] for every card (an empty list means no change badge). The description is not word-wrapped by the game
    // (UberText WordWrap is off and it shrinks to fit one line), so wrap it here: a few short lines make the biggest text that fits.
    internal static string Description(string header, string text)
    {
        int gap = text.IndexOf("\n\n", StringComparison.Ordinal);
        var lines = new List<string> { header + "  " + text.Substring(0, gap) };   // "<deck>: STRATEGY  (1/2)"
        lines.AddRange(NativeRecipeGuideText.Wrap(text.Substring(gap + 2)));
        return string.Join("\n", lines);
    }
    // The cards sit smaller so their tops clear the text block above them (up to five lines); measured live 2026-11-05 at 1920x1080. Moving the panel down or scaling its depth makes the card bodies vanish behind the parchment, so it is only scaled across the screen.
    private const float CardScale = .66f;

    // Escape closes the whole guide, like CLOSE. CardListPopup has no Escape handler of its own (DialogBase returns false, so the key would
    // open the Game Menu over the popup): a postfix on DialogBase.HandleKeyboardInput answers for the guide's own popup only.
    internal static void Install(HarmonyLib.Harmony harmony, string reports)
    {
        reportsDir = reports ?? "";
        var method = typeof(DialogBase).GetMethod("HandleKeyboardInput", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
        if (method != null) harmony.Patch(method, postfix: new HarmonyLib.HarmonyMethod(typeof(NativeGuideCards), nameof(EscapePostfix)));
    }

    private static void EscapePostfix(DialogBase __instance, ref bool __result)
    {
        try
        {
            var page = Current;
            if (__result || page == null || !page.Appeared || !(__instance is CardListPopup popup)) return;
            page.Advance = false;
            popup.Hide();
            __result = true;
        }
        catch (Exception error) { NativeUIProbe.LogRecipeFailure(new InvalidOperationException("guide-cards-escape", error)); }
    }

    private static void Abandon()
    {
        var page = Current; Current = null;
        if (page?.Watcher != null) UnityEngine.Object.Destroy(page.Watcher);
    }

    private static void Finish(Page page, bool advance)
    {
        if (page.Finished) return;
        page.Finished = true;
        if (Current == page) Current = null;
        if (page.Watcher != null) UnityEngine.Object.Destroy(page.Watcher);
        try { page.Done(advance); }
        catch (Exception error) { NativeUIProbe.LogRecipeFailure(new InvalidOperationException("guide-cards-done", error)); }
    }

    // The popup never appeared in time. Take the queued request back if it is still queued, keep the watcher so a popup that is already
    // loading is hidden the moment it shows (never a late default-labelled card page after the text page), and raise the text page.
    private static void Fail(Page page)
    {
        if (page.Finished) return;
        page.Finished = true; page.Abandoned = true;
        if (Current == page) Current = null;
        try { CancelQueued(page.Info); }
        catch (Exception error) { NativeUIProbe.LogRecipeFailure(new InvalidOperationException("guide-cards-cancel", error)); }
        try { page.Failed(); }
        catch (Exception error) { NativeUIProbe.LogRecipeFailure(new InvalidOperationException("guide-cards-failed", error)); }
    }

    private static void CancelQueued(object info)
    {
        var dialogs = DialogManager.Get();
        var field = typeof(DialogManager).GetField("m_dialogRequests", Private);
        var queue = dialogs == null ? null : field?.GetValue(dialogs);
        if (queue == null) return;
        var type = queue.GetType();
        int count = (int)type.GetProperty("Count").GetValue(queue);
        var infoField = typeof(DialogManager).GetNestedType("DialogRequest", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetField("m_info");
        if (infoField == null) return;
        for (int index = 0; index < count; index++)
        {
            object request = type.GetMethod("Dequeue").Invoke(queue, null);
            if (!ReferenceEquals(infoField.GetValue(request), info)) type.GetMethod("Enqueue").Invoke(queue, new[] { request });
        }
    }

    // Scale the panel across the screen but not along the camera's view axis: scaling depth too moves the card planes behind the parchment
    // and their bodies stop drawing (seen live at 0.76). The panel's own axes are not screen-aligned (it is tilted), so find the depth axis.
    private static void Shrink(Transform panel, Component popup)
    {
        var camera = CameraUtils.FindFirstByLayerMask(1 << popup.gameObject.layer);
        if (camera == null) return;
        var view = camera.transform.forward;
        var axes = new[] { panel.right, panel.up, panel.forward };
        int depth = 0;
        for (int axis = 1; axis < 3; axis++) if (Mathf.Abs(Vector3.Dot(axes[axis], view)) > Mathf.Abs(Vector3.Dot(axes[depth], view))) depth = axis;
        var size = panel.localScale;
        for (int axis = 0; axis < 3; axis++) if (axis != depth) size[axis] *= CardScale;
        panel.localScale = size;
    }

    // Page one also offers CLOSE (the text page's NEXT/CLOSE pair): a clone of the popup's one button, side by side with it. Fails closed to a
    // single NEXT button (Escape still closes).
    private static UIBButton AddClose(CardListPopup popup, UIBButton ok, Page page)
    {
        try
        {
            var camera = CameraUtils.FindFirstByLayerMask(1 << popup.gameObject.layer);
            var box = ok.GetComponentsInChildren<Collider>().Select(collider => collider.bounds).FirstOrDefault();
            if (camera == null || box.size == Vector3.zero) return null;
            var close = UnityEngine.Object.Instantiate(ok, ok.transform.parent);
            close.name = "SoloStoneGuideClose";
            close.ClearEventListeners();
            close.BubbleUpEvent = "";
            close.SetText("CLOSE");
            close.SetEnabled(true);
            close.AddEventListener(UIEventType.RELEASE, _ => { page.Advance = false; popup.Hide(); });
            var shift = camera.transform.right * (box.size.x * .6f);
            ok.transform.position -= shift;
            close.transform.position += shift;
            return close;
        }
        catch (Exception error) { NativeUIProbe.LogRecipeFailure(new InvalidOperationException("guide-cards-close", error)); return null; }
    }

    // Waits for the popup, labels and hooks its button, and ends the page when the popup is gone. One page at a time.
    private sealed class Watcher : MonoBehaviour
    {
        internal Page Page;
        private void Update()
        {
            var page = Page;
            try
            {
                if (page == null) { Destroy(gameObject); return; }
                var popup = FindFirstObjectByType<CardListPopup>();
                if (page.Abandoned)
                {
                    // A popup that was already loading when the guide gave up: hide it, never show it. Stop watching after a while.
                    if (popup != null && ReferenceEquals(typeof(CardListPopup).GetField("m_info", Private)?.GetValue(popup), page.Info)) { popup.Hide(); Destroy(gameObject); }
                    else if (Time.unscaledTime - page.Started > 30f) Destroy(gameObject);
                    return;
                }
                if (page.Finished || Current != page) { Destroy(gameObject); return; }
                if (popup != null && !page.Appeared)
                {
                    page.Appeared = true;
                    var ok = popup.m_okayButton;
                    ok.SetText(page.Next ? "NEXT" : "CLOSE");
                    foreach (var panel in popup.GetComponentsInChildren<CardListPanel>(true))
                        if (panel.gameObject.activeInHierarchy)
                        {
                            Shrink(panel.transform, popup);
                        }
                    ok.AddEventListener(UIEventType.RELEASE, _ => page.Advance = page.Next);
                    if (page.Next) page.Close = AddClose(popup, ok, page);
                }
                else if (popup == null && page.Appeared) Finish(page, page.Advance);   // destroyed without a hide callback
                else if (popup == null && Time.unscaledTime - page.Started > Timeout)
                {
                    NativeUIProbe.LogRecipeFailure(new TimeoutException("guide-cards: popup did not appear"));
                    Fail(page);
                }
            }
            catch (Exception error)
            {
                NativeUIProbe.LogRecipeFailure(new InvalidOperationException("guide-cards-watch", error));
                if (page != null) Fail(page);
            }
        }
    }

#if !SOLOSTONE_RELEASE
    // ui: the dialog node's card visuals and the one button's semantic id (guide.next / guide.close, as on the text page).
    internal static void Describe(CardListPopup popup, JsonNode node, Action<PegUIElement, string> name, Dictionary<PegUIElement, string> contexts)
    {
        var page = Current;
        if (page == null || popup == null) return;
        node.Add("id", page.PopupId); node.Add("header", page.Header); node.Add("text", page.Text);
        name(popup.m_okayButton, page.Next ? NativeDeckGuideText.NextId : NativeDeckGuideText.CloseId);
        if (page.Close != null) name(page.Close, NativeDeckGuideText.CloseId);
        foreach (PegUIElement button in popup.GetComponentsInChildren<PegUIElement>(true)) contexts[button] = page.Header + " " + page.Text;
        node.Add("cardList", State(popup));
    }
#endif

#if !SOLOSTONE_RELEASE
    // state.deckGuideCards and the dialog node: which card visuals are up, where (1280x720, top-left origin like `ui`), and what they touch.
    internal static JsonNode State(CardListPopup popup = null)
    {
        var page = Current;
        var result = new JsonNode { { "open", page != null }, { "appeared", page?.Appeared ?? false }, { "wanted", TestControlProtocol.List((page?.CardIds ?? new string[0]).Cast<object>().ToArray()) } };
        popup = popup ?? UnityEngine.Object.FindFirstObjectByType<CardListPopup>();
        result.Add("popups", (long)UnityEngine.Object.FindObjectsByType<CardListPopup>(FindObjectsSortMode.None).Length);
        result.Add("actors", (long)UnityEngine.Object.FindObjectsByType<CardListPanel>(FindObjectsSortMode.None).Sum(panel => panel.GetComponentsInChildren<Actor>(true).Count(actor => actor != null && actor.gameObject.activeInHierarchy)));
        if (page == null || popup == null) return result;
        try
        {
            var camera = CameraUtils.FindFirstByLayerMask(1 << popup.gameObject.layer);
            var panel = new[] { popup.m_CardsContainer_MultiLineDescription, popup.m_CardsContainer_SingleLineDescription }
                .FirstOrDefault(candidate => candidate != null && candidate.gameObject.activeInHierarchy);
            var button = Rect(popup.m_okayButton != null ? popup.m_okayButton.GetComponentsInChildren<Collider>().Select(c => c.bounds) : null, camera);
            // UberText lives in an unreferenced assembly and the actor list is private: read both by reflection, as a Component and a list.
            var description = typeof(CardListPopup).GetField("m_descriptionMultiLine", Private)?.GetValue(popup) as Component;
            var text = Rect(description == null ? null : description.GetComponentsInChildren<Renderer>().Where(r => r.enabled).Select(r => r.bounds), camera)
                ?? TextBox(description, camera);
            var cards = new JsonList();
            var rects = new List<(string, float[])>();
            int index = 0;
            foreach (var actor in popup.GetComponentsInChildren<Actor>(true).Where(a => a != null && a.gameObject.activeInHierarchy).OrderBy(a => a.transform.position.x))
            {
                if (actor == null || !actor.gameObject.activeInHierarchy) continue;
                var face = actor.m_cardMesh != null ? actor.m_cardMesh.GetComponentsInChildren<Renderer>() : actor.GetComponentsInChildren<Renderer>();
                var rect = Rect(face.Where(r => r.enabled).Select(r => r.bounds), camera);
                string id = actor.GetEntityDef()?.GetCardId();   // the actor's own card, not its position (the second card page starts again at 0)
                var overlaps = new List<object>();
                if (rect != null && button != null && Touch(rect, button)) overlaps.Add("button");
                if (rect != null && text != null && Touch(rect, text)) overlaps.Add("text");
                foreach (var (other, otherRect) in rects) if (rect != null && Covers(rect, otherRect)) overlaps.Add(other);
                bool inside = rect != null && rect[0] >= 0 && rect[1] >= 0 && rect[0] + rect[2] <= 1280 && rect[1] + rect[3] <= 720;
                cards.Add(new JsonNode { { "id", id }, { "rect", rect == null ? null : TestControlProtocol.List(rect.Select(v => (object)(long)Math.Round(v)).ToArray()) },
                    { "onScreen", inside }, { "overlaps", TestControlProtocol.List(overlaps.ToArray()) } });
                if (rect != null) rects.Add(("card:" + id, rect));
                index++;
            }
            result.Add("cards", cards);
            result.Add("button", button == null ? null : TestControlProtocol.List(button.Select(v => (object)(long)Math.Round(v)).ToArray()));
            result.Add("text", text == null ? null : TestControlProtocol.List(text.Select(v => (object)(long)Math.Round(v)).ToArray()));
        }
        catch (Exception error) { result.Add("error", error.Message); }
        return result;
    }
#endif

    private const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
#if !SOLOSTONE_RELEASE
    // The text box itself (UberText Width x Height in its local units, top anchored), for a text with no enabled renderer.
    private static float[] TextBox(Component description, Camera camera)
    {
        if (description == null || camera == null) return null;
        float width = Convert.ToSingle(description.GetType().GetProperty("Width")?.GetValue(description) ?? 0f);
        float height = Convert.ToSingle(description.GetType().GetProperty("Height")?.GetValue(description) ?? 0f);
        var scale = description.transform.lossyScale; var center = description.transform.position;
        var box = new Bounds(center - new Vector3(0, height * scale.y / 2, 0), new Vector3(width * scale.x, height * scale.y, .1f));
        return Rect(new[] { box }, camera);
    }
#endif

    // Two cards overlap when more than a fifth of the smaller one is shared (the face meshes carry a small margin).
    private static bool Covers(float[] a, float[] b)
    {
        float w = Math.Min(a[0] + a[2], b[0] + b[2]) - Math.Max(a[0], b[0]), h = Math.Min(a[1] + a[3], b[1] + b[3]) - Math.Max(a[1], b[1]);
        return w > 0 && h > 0 && w * h > .2f * Math.Min(a[2] * a[3], b[2] * b[3]);
    }

    private static bool Touch(float[] a, float[] b) => a[0] < b[0] + b[2] && b[0] < a[0] + a[2] && a[1] < b[1] + b[3] && b[1] < a[1] + a[3];

    // Screen rect of world bounds on the dialog's camera, normalized to 1280x720 with a top-left origin (the `ui` convention).
    private static float[] Rect(IEnumerable<Bounds> boxes, Camera camera)
    {
        if (boxes == null || camera == null) return null;
        float left = float.MaxValue, right = float.MinValue, bottom = float.MaxValue, top = float.MinValue;
        bool any = false;
        foreach (var bounds in boxes)
            for (int corner = 0; corner < 8; corner++)
            {
                var point = camera.WorldToScreenPoint(new Vector3((corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y, (corner & 4) == 0 ? bounds.min.z : bounds.max.z));
                left = Math.Min(left, point.x); right = Math.Max(right, point.x); bottom = Math.Min(bottom, point.y); top = Math.Max(top, point.y); any = true;
            }
        if (!any) return null;
        float width = Screen.width, height = Screen.height;
        return new[] { left * 1280f / width, (height - top) * 720f / height, (right - left) * 1280f / width, (top - bottom) * 720f / height };
    }
}
