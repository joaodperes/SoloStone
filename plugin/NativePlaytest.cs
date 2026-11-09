using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using SabberStoneCore.Tasks.PlayerTasks;

// Opt-in diagnostics only: exports advice/coordinates; never calls native input, Process or profile mutation.
internal static class NativePlaytest
{
#if SOLOSTONE_RELEASE
    internal static string Root = "";
#else
    internal static string Root = @"C:\hs-native-ui-"+@"prototype\reports\";
#endif
    private static float next;
#if STANDARD_PROOF
    private static string proofRunId;
#endif
    private static string matchId, previous;
    private static int options = -1;
    private static BotAction advice;
#if SOLOSTONE_INSTALLER && !SOLOSTONE_TEST_HOOKS
    public static string DebugStatus => "enabled=" + false + " next=" + next + " options=" + options + " advice=" + advice;
#else
    public static string DebugStatus => "enabled=" + File.Exists(Root + "enable-playtest") + " next=" + next + " options=" + options + " advice=" + advice;
#endif
    public static void Update(NativeMatch match, string runId)
    {
        if (Time.realtimeSinceStartup < next) return;
        next = Time.realtimeSinceStartup + .2f;
#if STANDARD_PROOF
        proofRunId=runId;
        WriteNavigation(runId);
#endif
#if SOLOSTONE_INSTALLER && !SOLOSTONE_TEST_HOOKS
#pragma warning disable CS0162
        return; // An installer release writes no playtest records.
#else
        if (!File.Exists(Root + "enable-playtest")) return;
#endif
#if !SOLOSTONE_RELEASE
        string record;
        try { record = Build(match); }
        catch (Exception error) { record = "ready=false\nreason=" + error.GetType().Name + ":" + error.Message; }
        bool stable = record == previous;
        previous = record;
        string contents = "runId=" + runId + "\nmatchId=" + LocalLadder.MatchId
#if STANDARD_PROOF
            + "\nsnapshot=" + StandardProofNative.Snapshot
#endif
            + "\nutc=" + DateTime.UtcNow.ToString("o") + "\n" + (stable ? record : "ready=false\nreason=waiting for stable coordinates");
        try
        {
            File.WriteAllText(Root + "playtest.tmp", contents);
            if (File.Exists(Root + "playtest.txt")) File.Replace(Root + "playtest.tmp",Root + "playtest.txt",null);
            else File.Move(Root + "playtest.tmp",Root + "playtest.txt");
        }
        catch (IOException) { /* Reader can briefly hold the previous record; retry next tick. */ }
#endif
    }
#if !SOLOSTONE_RELEASE
    private static string Build(NativeMatch match)
    {
        if (LocalLadder.Screen != "Playing" || match?.Game == null) return Block("outside match");
        if (matchId != LocalLadder.MatchId) { matchId = LocalLadder.MatchId; options = -1; advice = null; }
        var front = GameState.Get(); var input = InputManager.Get();
        if (front == null || input == null || Camera.main == null) return Block("native objects loading");
        if (front.IsMulliganManagerActive())
        {
            var button = MulliganManager.Get()?.GetMulliganButton()?.GetComponent<Collider>();
            if (front.IsMulliganBusy() || button == null || !button.enabled) return Block("mulligan intro");
            return Click("mulligan", "keep", Point(button));
        }
        if (match.Game.State == SabberStoneCore.Enums.State.COMPLETE || match.Game.CurrentPlayer != match.Game.Player1)
            return Block("not human turn");
        if (!input.IsInputEnabled() || UniversalInputManager.Get() == null || UniversalInputManager.Get().IsDialogActive()
            || front.IsBusy() || front.IsResponsePacketBlocked() || front.IsMulliganBusy()
            || front.GetPowerProcessor().GetPowerQueue().Count != 0) return Block("native input busy");
        if (front.GetOptionsPacket() == null || front.GetOptionsPacket().ID != match.HumanOptionsId) return Block("options not synchronized");
#if STANDARD_PROOF
        if (!match.IsProofMatch)
#endif
#if !SOLOSTONE_INSTALLER || SOLOSTONE_TEST_HOOKS
        if (match.Game.Player1.BaseMana >= 6 && File.Exists(Root + "benchmark-ranks"))
        {
            var rows = new System.Collections.Generic.List<string> { "utc,stars,depth,nodes,totalMilliseconds" };
            for (int sample = 0; sample < 5; sample++)
                foreach (int stars in new[] { 0,30,60,90,120,150 })
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var root = FairBotState.Create(match.Game,1,8123+sample,DeckStyle.Tempo);
                    var plan = BotSearch.Choose(root,BotBudget.ForStars(stars));
                    rows.Add(DateTime.UtcNow.ToString("o")+","+stars+","+plan.Depth+","+plan.Nodes+","+timer.ElapsedMilliseconds);
                }
            File.WriteAllLines(Root + "rank-benchmark.csv",rows);
            File.Delete(Root + "benchmark-ranks");
        }
#endif
#if STANDARD_PROOF
        WriteStableTradeState(match,front);
#endif
        if (advice == null || options != match.HumanOptionsId)
        {
            options = match.HumanOptionsId;
#if STANDARD_PROOF
            if (match.IsProofMatch) advice = BotAction.From(match.HumanProofBot.Choose(match.Game.Player1.Options()));
            else
#endif
            advice = BotSearch.Choose(FairBotState.Create(match.Game, 1, 319 * match.Game.Turn + options, DeckStyle.Tempo),
                new BotBudget(6,14,1000,150)).Action;
        }
        string nonce = options + ":" + advice;
        if (front.GetResponseMode() == GameState.ResponseMode.OPTION_TARGET)
        {
            var target = front.GetEntity(advice.Target);
            if (target == null || front.GetSelectedNetworkOption()?.Main.ID != advice.Source
                || !front.IsValidOptionTarget(target,true)) return Block("target phase not synchronized");
            return Click("target",nonce,Point(target.GetCard()?.GetActor()?.GetCollider()));
        }
        var end = EndTurnButton.Get();
        if (front.GetResponseMode() != GameState.ResponseMode.OPTION || input.GetHeldCard() != null
            || input.GetBattlecrySourceCard() != null || end == null || end.IsInputBlocked() || end.IsDisabled
            || end.GetComponent<Collider>()?.enabled != true) return Block("turn animation");
        if (advice.Type == PlayerTaskType.END_TURN) return Click("main",nonce,Point(end.GetComponent<Collider>()));
        var source = front.GetEntity(advice.Source);
        if (source == null || !front.HasResponse(source) || !front.IsEntityInputEnabled(source)) return Block("source not ready");
        Vector2 from;
        if (advice.Type == PlayerTaskType.PLAY_CARD
#if STANDARD_PROOF
            || advice.Type == PlayerTaskType.TRADE_CARD
#endif
        )
        {
            var card = source.GetCard(); var hand = front.GetFriendlySidePlayer().GetHandZone();
            if (card == null || !card.CardStandInIsInteractive()) return Block("hand not interactive");
            from = Point(hand.GetStandIn(card)?.standInCollider);
        }
        else from = Point(source.GetCard()?.GetActor()?.GetCollider());
        if (advice.Type == PlayerTaskType.HERO_POWER) return Click("main",nonce,from);
        Vector2 to;
        bool minion = advice.Type == PlayerTaskType.PLAY_CARD
            && match.Game.IdEntityDic[advice.Source] is SabberStoneCore.Model.Entities.Minion;
#if STANDARD_PROOF
        if (advice.Type == PlayerTaskType.TRADE_CARD) to = TradeDropPoint();
        else
#endif
        if (advice.Target != 0 && !minion) to = Point(front.GetEntity(advice.Target)?.GetCard()?.GetActor()?.GetCollider());
        else
        {
            var zone = front.GetFriendlySidePlayer().GetBattlefieldZone();
            var plane = Board.Get()?.FindCollider("DragPlane");
            if (plane == null) return Block("drop plane missing");
            var world = plane.bounds.center;
            world.x = minion ? zone.transform.position.x
                - (zone.GetCards().Count + 1) * zone.GetSlotWidth() / 2
                + (advice.Position + .5f) * zone.GetSlotWidth() : zone.transform.position.x;
            world.z = zone.transform.position.z;
            to = Project(world);
            var pixel = new Vector3(to.x * Screen.width / 1280,(720-to.y)*Screen.height/720,0);
            RaycastHit drop;
            if (!CameraUtils.Raycast(Camera.main,pixel,1 << plane.gameObject.layer,out drop) || drop.collider != plane)
                return Block("planned insertion slot not hittable");
        }
        return "ready=true\nphase=main\nnonce=" + nonce + "\naction=drag\nfrom=" + Coordinates(from) + "\nto=" + Coordinates(to)
#if STANDARD_PROOF
            + "\noptionId=" + options + "\noptionIndex=" + match.ProofOptionIndex(advice)
            + "\nkeyword=" + (advice.Type == PlayerTaskType.TRADE_CARD ? 1720 : 0)
            + "\nsource=" + advice.Source + "\ncardId=" + match.Game.IdEntityDic[advice.Source].Card.Id
#endif
            ;
    }
#endif
#if STANDARD_PROOF
    private static int stableTradeAction;
    private static string stableTradeMatch;
    private static void WriteStableTradeState(NativeMatch match,GameState front)
    {
        if (match.LastTradeAction <= 0 || (stableTradeMatch==LocalLadder.MatchId && stableTradeAction==match.LastTradeAction)) return;
        int Count(int player,int zone) => front.GetEntityMap().Values.Count(entity => entity.GetTag(GAME_TAG.CONTROLLER)==player && entity.GetTag(GAME_TAG.ZONE)==zone);
        int hand=Count(1,3), deck=Count(1,2), enemyHand=Count(2,3), enemyDeck=Count(2,2);
        var source=front.GetEntity(match.LastTradeSource);
        var engineSource=match.Game.IdEntityDic[match.LastTradeSource];
        if (source==null || source.GetTag((GAME_TAG)2045)!=0 || source.GetTag(GAME_TAG.ZONE)!=(int)engineSource.Zone.Type
            || hand!=match.Game.Player1.HandZone.Count || deck!=match.Game.Player1.DeckZone.Count
            || enemyHand!=match.Game.Player2.HandZone.Count || enemyDeck!=match.Game.Player2.DeckZone.Count
            || front.GetFriendlySidePlayer().GetNumAvailableResources()!=match.Game.Player1.RemainingMana) return;
        string record="{\"phase\":\"stable\",\"action\":"+match.LastTradeAction+",\"source\":"+match.LastTradeSource
            +",\"sourceZone\":"+source.GetTag(GAME_TAG.ZONE)+",\"usingTradeOption\":"+source.GetTag((GAME_TAG)2045)
            +",\"humanHand\":"+hand+",\"humanDeck\":"+deck+",\"enemyHand\":"+enemyHand+",\"enemyDeck\":"+enemyDeck
            +",\"nativeMana\":"+front.GetFriendlySidePlayer().GetNumAvailableResources()+",\"engineMana\":"+match.Game.Player1.RemainingMana+"}";
        File.AppendAllText(Root+"trade-proof.jsonl",StandardProofNative.Envelope(proofRunId,LocalLadder.MatchId,record)+"\n");
        stableTradeMatch=LocalLadder.MatchId; stableTradeAction=match.LastTradeAction;
    }
    private static Vector2 TradeDropPoint()
    {
        var area = Board.Get()?.FindCollider("DeckActionArea");
        var plane = Board.Get()?.FindCollider("DragPlane");
        if (area == null || plane == null || Camera.main == null || !area.enabled || !plane.enabled
            || !area.gameObject.activeInHierarchy || !plane.gameObject.activeInHierarchy)
            throw new InvalidOperationException("Trade drop area/plane unavailable.");
        for (int sample = 0; sample < 26; sample++)
        {
            var world = area.bounds.center;
            if (sample > 0)
            {
                int grid = sample - 1;
                world.x += area.bounds.extents.x * ((grid % 5 - 2) * .3f);
                world.z += area.bounds.extents.z * ((grid / 5 - 2) * .3f);
            }
            var pixel = Camera.main.WorldToScreenPoint(world);
            if (pixel.z <= 0 || pixel.x < 1 || pixel.x >= Screen.width-1 || pixel.y < 1 || pixel.y >= Screen.height-1) continue;
            RaycastHit hit;
            if (CameraUtils.Raycast(Camera.main,pixel,1 << 9,out hit)
                && hit.collider == plane && area.bounds.Contains(hit.point) && Card.IsInDeckActionArea(hit.point))
                return new Vector2(pixel.x*1280/Screen.width,(Screen.height-pixel.y)*720/Screen.height);
        }
        throw new InvalidOperationException("No DragPlane hit inside Trade area.");
    }
    private static void WriteNavigation(string runId)
    {
        var rows = new System.Collections.Generic.List<string> {
            "runId="+runId, "snapshot="+StandardProofNative.Snapshot, "matchId="+LocalLadder.MatchId,
            "utc="+DateTime.UtcNow.ToString("o"), "scene="+(SceneMgr.IsInitialized()?SceneMgr.Get().GetMode().ToString():"uninitialized")
        };
        try
        {
            if (!SceneMgr.IsInitialized() || SceneMgr.Get().IsTransitioning()) throw new InvalidOperationException("scene transition");
            if (SceneMgr.Get().GetMode() == SceneMgr.Mode.TOURNAMENT)
            {
                var pickerCamera=Box.Get()?.GetCamera();
                if (pickerCamera==null) throw new InvalidOperationException("Picker camera unavailable.");
                foreach (string key in new[]{"proof-mage","proof-warrior"})
                {
                    long id = NativeUIProjection.DeckId(key);
                    var tiles = UnityEngine.Object.FindObjectsByType<CollectionDeckBoxVisual>(FindObjectsSortMode.None)
                        .Where(tile => tile.GetDeckID() == id && tile.GetCollectionDeck()?.ID == id
                            && tile.gameObject.activeInHierarchy && tile.IsEnabled() && tile.CanSelectDeck()
                            && tile.IsDeckPlayable() && tile.GetComponent<BoxCollider>()?.enabled == true).ToArray();
                    if (tiles.Length != 1) { rows.Add(key+"=blocked:exact visible selectable tile unavailable"); continue; }
                    rows.Add(key+"="+Coordinates(Point(tiles[0].GetComponent<BoxCollider>(),pickerCamera)));
                }
                var tray = DeckPickerTrayDisplay.Get();
                var play = AccessTools.Field(typeof(AbsDeckPickerTrayDisplay),"m_playButton").GetValue(tray) as PlayButton;
                if (play != null && play.gameObject.activeInHierarchy && play.IsEnabled())
                    rows.Add("play="+Coordinates(Point(play.GetComponent<Collider>(),pickerCamera)));
            }
            var menu = GameMenu.Get();
            if (menu != null && menu.GameMenuIsShown())
            {
                var quit = AccessTools.Field(typeof(GameMenu),"m_quitButton").GetValue(menu) as UIBButton;
                var quitCamera=CameraUtils.GetMainCamera();
                if (quit != null && quitCamera != null && quit.gameObject.activeInHierarchy && quit.IsEnabled())
                    rows.Add("quit="+Coordinates(Point(quit.GetComponent<Collider>(),quitCamera)));
            }
        }
        catch (Exception error) { rows.Add("blocked="+error.GetType().Name+":"+error.Message); }
        try
        {
            File.WriteAllLines(Root+"standard-ui-targets.tmp",rows);
            if (File.Exists(Root+"standard-ui-targets.txt")) File.Replace(Root+"standard-ui-targets.tmp",Root+"standard-ui-targets.txt",null);
            else File.Move(Root+"standard-ui-targets.tmp",Root+"standard-ui-targets.txt");
        }
        catch(IOException) { /* Retry next tick if a reader holds the previous record. */ }
    }
#endif
    private static string Block(string why) => "ready=false\nreason=" + why;
    private static string Click(string phase,string nonce,Vector2 point) => "ready=true\nphase=" + phase + "\nnonce=" + nonce
        + "\naction=click\nfrom=" + Coordinates(point)
#if STANDARD_PROOF
        + (phase == "mulligan" ? "" : "\noptionId=" + options)
#endif
        ;
    private static string Coordinates(Vector2 p) => Math.Round(p.x) + "," + Math.Round(p.y);
    private static Vector2 Project(Vector3 world)
    {
        var point = Camera.main.WorldToScreenPoint(world);
        if (point.z <= 0 || point.x < 0 || point.x >= Screen.width || point.y < 0 || point.y >= Screen.height)
            throw new InvalidOperationException("Collider projects outside client.");
        return new Vector2(point.x * 1280 / Screen.width,(Screen.height - point.y) * 720 / Screen.height);
    }
#if !SOLOSTONE_RELEASE
    // Aim points for a hand card's stand-in collider, best first: the client picks hand cards through these colliders (InputManager.HandleLeftMouseDown),
    // and a crowded fan's neighbours cover most of each one. See TestControlRules.BestAimCells; each result is {x, y, clearance} in the 1280x720 frame.
    internal static List<int[]> AimPoints(Collider collider, int count, Camera camera = null)
    {
        camera = camera ?? Camera.main;
        if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) throw new InvalidOperationException("Collider not interactive.");
        if (camera == null) throw new InvalidOperationException("Input camera unavailable.");
        const int Grid = 17;
        var bounds = collider.bounds;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int corner = 0; corner < 8; corner++)
        {
            var point = camera.WorldToScreenPoint(new Vector3(
                (corner & 1) == 0 ? bounds.min.x : bounds.max.x, (corner & 2) == 0 ? bounds.min.y : bounds.max.y, (corner & 4) == 0 ? bounds.min.z : bounds.max.z));
            minX = Mathf.Min(minX, point.x); maxX = Mathf.Max(maxX, point.x); minY = Mathf.Min(minY, point.y); maxY = Mathf.Max(maxY, point.y);
        }
        minX = Mathf.Max(1, minX); minY = Mathf.Max(1, minY); maxX = Mathf.Min(Screen.width - 2, maxX); maxY = Mathf.Min(Screen.height - 2, maxY);
        var own = new bool[Grid, Grid];
        int mask = 1 << collider.gameObject.layer;
        for (int x = 0; x < Grid; x++)
            for (int y = 0; y < Grid; y++)
            {
                var pixel = new Vector3(Mathf.Lerp(minX, maxX, (x + .5f) / Grid), Mathf.Lerp(minY, maxY, (y + .5f) / Grid), 0);
                RaycastHit hit;
                own[x, y] = CameraUtils.Raycast(camera, pixel, mask, out hit) && hit.collider == collider;
            }
        var result = new List<int[]>();
        foreach (var cell in TestControlRules.BestAimCells(own, count))
            result.Add(new[] {
                (int)Math.Min(1279, Math.Round(Mathf.Lerp(minX, maxX, (cell[0] + .5f) / Grid) * 1280 / Screen.width)),
                (int)Math.Min(719, Math.Round((Screen.height - Mathf.Lerp(minY, maxY, (cell[1] + .5f) / Grid)) * 720 / Screen.height)), cell[2] });
        return result;
    }
    internal static Vector2 Point(Collider collider, Camera camera = null)
    {
        camera = camera ?? Camera.main;
        if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
            throw new InvalidOperationException("Collider not interactive.");
        var bounds = collider.bounds;
        if (camera == null) throw new InvalidOperationException("Input camera unavailable.");
        var low = camera.WorldToScreenPoint(bounds.min);
        var high = camera.WorldToScreenPoint(bounds.max);
        var center = camera.WorldToScreenPoint(bounds.center);
        for (int i = 0; i < 26; i++)
        {
            var point = center;
            if (i > 0)
            {
                int grid = i - 1;
                point.x = Mathf.Lerp(Mathf.Max(1,Mathf.Min(low.x,high.x)),Mathf.Min(Screen.width-2,Mathf.Max(low.x,high.x)),(grid%5+.5f)/5);
                point.y = Mathf.Lerp(Mathf.Max(1,Mathf.Min(low.y,high.y)),Mathf.Min(Screen.height-2,Mathf.Max(low.y,high.y)),(grid/5+.5f)/5);
            }
            if (point.z <= 0 || point.x < 0 || point.x >= Screen.width || point.y < 0 || point.y >= Screen.height) continue;
            RaycastHit hit;
            if (CameraUtils.Raycast(camera,point,1 << collider.gameObject.layer,out hit) && hit.collider == collider)
                return new Vector2(point.x*1280/Screen.width,(Screen.height-point.y)*720/Screen.height);
        }
        throw new InvalidOperationException("Collider has no unobstructed screen point.");
    }
#endif
}
