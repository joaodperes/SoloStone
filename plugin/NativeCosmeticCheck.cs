#if !SOLOSTONE_RELEASE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Blizzard.T5.Services;
using Hearthstone.UI;
using MiniJSON;
using UnityEngine;

// One owned instance, one native operation per frame. Raw prefabs remain owned by the native loader.
// Slow native steps are recorded as warnings, never failures: retail shop first loads are slow too.
internal sealed class NativeCosmeticCheck : MonoBehaviour
{
    private static NativeCosmeticCheck current;
    private const double ItemSeconds = 25;
    private const double SlowStepMs = 100;
    private string runId;
    private LocalShopProduct[] products;
    private readonly JsonList results = new JsonList();
    private readonly List<GameObject> instances = new List<GameObject>();
    private readonly List<IDisposable> assetHandles = new List<IDisposable>();
    private DefLoader.DisposableCardDef cardDef;
    private IEnumerator item;
    private JsonNode details;
    private DateTime deadline;
    private int index, generation;
    private bool complete;
    private string stage;

    internal static void RequireHub()
    {
        NativeTestControl.AssertMainThread();
        var profile = LocalLadder.Profile;
        if (!NativeUIProbe.ShopContextReady || !SceneMgr.IsInitialized() || SceneMgr.Get().GetMode() != SceneMgr.Mode.HUB
            || LocalLadder.Screen != "Menu" || profile == null || profile.Pending != null
            || profile.PendingRewardPresentation != null || profile.PackRevealPending)
            throw new InvalidOperationException("Cosmetic checks require idle HUB with no match or pending presentation");
    }

    internal static JsonNode Start(string[] ids)
    {
        RequireHub();
        if (current != null && !current.complete) throw new InvalidOperationException("Cosmetic check already running: " + current.runId);
        var all = LocalShop.Default.Products.Where(p => p.Kind == "CardBack" || p.Kind == "Coin" || p.Kind == "HeroSkin" || p.Kind == "Pet" && p.Pet != null).ToArray();
        var selected = ids.Length == 0 ? all : ids.Select(id => all.SingleOrDefault(p => p.Id == id)
            ?? AchievementExclusiveCosmetics.DiagnosticProduct(id)
            ?? throw new InvalidOperationException("Unknown cosmetic product: " + id)).ToArray();
        if (current != null) UnityEngine.Object.Destroy(current.gameObject);
        current = new GameObject("SoloStone cosmetic asset check").AddComponent<NativeCosmeticCheck>();
        UnityEngine.Object.DontDestroyOnLoad(current.gameObject);
        current.runId = Guid.NewGuid().ToString("N");
        current.products = selected;
        return current.Snapshot();
    }

    internal static JsonNode Status(string id)
    {
        NativeTestControl.AssertMainThread();
        if (current == null || current.runId != id) throw new InvalidOperationException("Unknown cosmetic run in this client launch");
        return current.Snapshot();
    }

    private JsonNode Snapshot() => new JsonNode {
        { "runId", runId }, { "complete", complete }, { "total", products.Length }, { "completed", results.Count },
        { "currentProductId", complete || index >= products.Length ? null : products[index].Id },
        { "stage", stage }, { "itemTimeoutSeconds", ItemSeconds }, { "slowStepWarningMs", SlowStepMs },
        { "results", TestControlProtocol.List(results) }
    };

    private void Update()
    {
        if (complete || products == null) return;
        NativeTestControl.AssertMainThread();
        try { RequireHub(); if (!NativeTestControl.DiagnosticsEnabled) throw new InvalidOperationException("Test control disabled"); }
        catch (Exception error)
        {
            Finish(error.Message);
            while (index < products.Length) Finish("Run interrupted: " + error.Message);
            return;
        }
        if (item == null)
        {
            if (index >= products.Length) { complete = true; return; }
            details = new JsonNode { { "stageMaxMs", new JsonNode() } };
            deadline = DateTime.UtcNow.AddSeconds(products[index].Kind == "Pet" ? ItemSeconds * 4 : ItemSeconds);
            item = Check(products[index]);
        }
        if (DateTime.UtcNow >= deadline) { Finish("Timeout at " + stage); return; }
        var watch = Stopwatch.StartNew();
        try
        {
            bool pending = item.MoveNext();
            Time(watch.Elapsed.TotalMilliseconds);
            if (!pending) Finish(null);
        }
        catch (Exception error) { Time(watch.Elapsed.TotalMilliseconds); Finish(stage + ": " + (error.InnerException ?? error).Message); }
    }

    // Per-stage maximum and overall maximum step time; informational only.
    private void Time(double elapsed)
    {
        double ms = Math.Round(elapsed, 1);
        var stages = (JsonNode)details["stageMaxMs"];
        string at = stage ?? "start";
        if (!stages.TryGetValue(at, out object old) || Convert.ToDouble(old) < ms) stages[at] = ms;
        if (!details.TryGetValue("maxStepMs", out object max) || Convert.ToDouble(max) < ms)
        {
            details["maxStepMs"] = ms;
            details["maxStepStage"] = at;
        }
    }

    private static JsonList Warnings(JsonNode facts)
    {
        var warnings = new JsonList();
        if (facts != null && facts.TryGetValue("stageMaxMs", out object value) && value is JsonNode stages)
            foreach (var pair in stages.Where(p => Convert.ToDouble(p.Value) > SlowStepMs).OrderByDescending(p => Convert.ToDouble(p.Value)))
                warnings.Add("Slow native step " + Convert.ToDouble(pair.Value).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                    + " ms at " + pair.Key + " (not a failure)");
        return warnings;
    }

    private void Finish(string reason)
    {
        if (products == null || index >= products.Length) { complete = true; return; }
        var product = products[index++];
        var facts = details ?? new JsonNode();
        results.Add(new JsonNode { { "productId", product.Id }, { "kind", product.Kind }, { "ok", reason == null },
            { "reason", reason }, { "warnings", Warnings(facts) }, { "details", facts } });
        Cleanup();
        details = null;
        complete = index == products.Length;
    }

    private void Cleanup()
    {
        generation++; // Late callbacks may not attach objects/defs to another item or a completed run.
        (item as IDisposable)?.Dispose(); item = null;
        foreach (var instance in instances) if (instance != null) UnityEngine.Object.Destroy(instance);
        instances.Clear();
        cardDef?.Dispose(); cardDef = null;
        foreach (var handle in assetHandles) handle.Dispose();
        assetHandles.Clear();
    }

    private void OnDestroy() { Cleanup(); }

    // Asynchronous raw load only; the native loader keeps the raw prefab, so later synchronous native instantiation is warm.
    private IEnumerator Raw(string path, Action<GameObject> loaded)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Missing prefab path");
        bool received = false;
        GameObject raw = null;
        int expected = generation;
        NativeRewardUI.LoadRaw(path, value => {
            NativeTestControl.AssertMainThread();
            if (this == null || generation != expected) return;
            raw = value; received = true;
        });
        while (!received) yield return null;
        if (raw == null) throw new InvalidOperationException("Prefab unavailable offline: " + path);
        loaded(raw);
    }

    private IEnumerator Prefab(string path, Action<GameObject> loaded)
    {
        stage = "prefab " + path;
        GameObject raw = null;
        var load = Raw(path, value => raw = value);
        while (load.MoveNext()) yield return null;
        yield return null;
        var instance = UnityEngine.Object.Instantiate(raw, new Vector3(1000, -1000, -1000), Quaternion.identity);
        if (instance == null) throw new InvalidOperationException("Prefab did not instantiate: " + path);
        instances.Add(instance);
        loaded(instance);
    }

    private IEnumerator Asset<T>(string path, Action<T> loaded, int options = 0) where T : UnityEngine.Object
    {
        stage = typeof(T).Name + " " + path;
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Missing " + typeof(T).Name + " asset path");
        object loader = NativeUIProbe.RewardAssetLoader ?? throw new InvalidOperationException("Native asset loader unavailable");
        // Asset-manager types are absent from the compile reference set; use the pinned callback overload.
        var method = loader.GetType().GetMethods().Single(m => m.Name == "LoadAsset" && m.IsGenericMethodDefinition
            && m.GetParameters().Length == 4).MakeGenericMethod(typeof(T));
        var parameters = method.GetParameters();
        var reference = parameters[0].ParameterType.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(string) }, null).Invoke(null, new object[] { path });
        bool received = false;
        T asset = null;
        int expected = generation;
        Action<object> callback = handle => {
            NativeTestControl.AssertMainThread();
            var lease = handle as IDisposable;
            if (this == null || generation != expected) { lease?.Dispose(); return; }
            if (lease != null) assetHandles.Add(lease);
            asset = handle?.GetType().GetProperty("Asset").GetValue(handle, null) as T;
            received = true;
        };
        var args = parameters[1].ParameterType.GetMethod("Invoke").GetParameters()
            .Select(p => Expression.Parameter(p.ParameterType)).ToArray();
        var nativeCallback = Expression.Lambda(parameters[1].ParameterType,
            Expression.Invoke(Expression.Constant(callback), Expression.Convert(args[1], typeof(object))), args).Compile();
        if (!(bool)method.Invoke(loader, new[] { reference, nativeCallback, null, Enum.ToObject(parameters[3].ParameterType, options) }))
            throw new InvalidOperationException("Async asset request rejected: " + path);
        while (!received) yield return null;
        if (asset == null) throw new InvalidOperationException("Missing " + typeof(T).Name.ToLowerInvariant() + " asset offline: " + path);
        loaded(asset);
        yield return null;
    }

    private IEnumerator Check(LocalShopProduct product)
    {
        if (product.Kind == "Pet") {
            var pet = CheckPet(product);
            while (pet.MoveNext()) yield return null;
            yield break;
        }
        if (product.Kind == "CardBack")
        {
            stage = "CardBack DBF";
            var record = GameDbf.CardBack?.GetRecord(product.CardBack.Value);
            if (record == null) throw new InvalidOperationException("Missing CardBack DBF record");
            details["prefab"] = record.PrefabName;
            GameObject backObject = null, actorObject = null;
            var load = Prefab(record.PrefabName, value => backObject = value);
            while (load.MoveNext()) yield return null;
            var back = backObject.GetComponentInChildren<CardBack>(true);
            if (back == null || back.m_CardBackMesh == null || back.m_CardBackTexture == null || back.m_CardBackMaterial == null)
                throw new InvalidOperationException("Missing CardBack component, mesh, texture or material");
            load = Prefab("Card_Hidden.prefab:1a94649d257bc284ca6e2962f634a8b9", value => actorObject = value);
            while (load.MoveNext()) yield return null;
            stage = "card back actor binding";
            var actor = actorObject.GetComponent<Actor>();
            if (actor == null || actor.m_cardMesh == null) throw new InvalidOperationException("Missing hidden Actor/card mesh");
            // Same binding as LoadCardBackActorByPrefab, with both prefabs requested asynchronously first.
            CardBackManager.SetCardBack(actor.m_cardMesh, back, false, false);
            actor.SetCardbackUpdateIgnore(true);
            back.transform.parent = actorObject.transform;
            yield return null;
            if (!actorObject.GetComponentsInChildren<Renderer>(true).Any(r => r.sharedMaterials.Any(m => m != null)))
                throw new InvalidOperationException("Bound card back has no renderer material");
            details["texture"] = back.m_CardBackTexture.name;
            details["material"] = back.m_CardBackMaterial.name;
            yield break;
        }
        bool coin = product.Kind == "Coin";
        stage = coin ? "coin DBF/EntityDef" : "hero DBF/EntityDef";
        var coinRecord = coin ? GameDbf.CosmeticCoin?.GetRecord(product.Coin.Value) : null;
        string cardId = coin ? coinRecord?.CardRecord?.NoteMiniGuid : product.HeroSkin;
        details["cardId"] = cardId;
        int dbId = GameUtils.TranslateCardIdToDbId(cardId);
        if (dbId <= 0 || GameDbf.Card?.GetRecord(dbId) == null || !coin && GameUtils.GetCardHeroRecordForCardId(dbId) == null)
            throw new InvalidOperationException("Missing card or CARD_HERO DBF record");
        var loader = DefLoader.Get() ?? throw new InvalidOperationException("DefLoader unavailable");
        var entity = loader.GetEntityDef(cardId);
        if (entity == null) throw new InvalidOperationException("Missing EntityDef");
        yield return null;
        stage = "CardDef";
        bool receivedDef = false;
        int expectedDef = generation;
        loader.LoadCardDefAsync(cardId, (id, definition, user) => {
            NativeTestControl.AssertMainThread();
            if (this == null || expectedDef != generation) { definition?.Dispose(); return; }
            cardDef = definition; receivedDef = true;
        });
        while (!receivedDef) yield return null;
        if (cardDef?.CardDef == null) throw new InvalidOperationException("CardDef unavailable offline");
        var def = cardDef.CardDef;
        // Earn-only skins never enter the Shop loader. Their Collection detail popup deliberately falls back to
        // CardDef portrait art when CollectionHeroDef.m_previewMaterial is absent.
        var preview = coin ? CoinPreview(coinRecord) : AchievementExclusiveCosmetics.ContainsHeroSkin(product.HeroSkin)
            ? CollectionPreview(def) : ShopPreview(def);
        while (preview.MoveNext()) yield return null;
        // Hand/play actors: what Collection's hero page and gameplay show (CardDef portrait, or m_LegendaryModel render texture).
        bool legendary = !string.IsNullOrEmpty(def.m_LegendaryModel);
        if (legendary)
        {
            stage = "legendary model " + def.m_LegendaryModel;
            details["legendaryModel"] = def.m_LegendaryModel;
            var warm = Raw(def.m_LegendaryModel, value => { });
            while (warm.MoveNext()) yield return null;
        }
        // UpdateMaterials/GetPortraitTexture only touch cached assets after these leases complete.
        foreach (var premium in def.m_AlwaysRenderGoldenPortrait
            ? new[] { TAG_PREMIUM.NORMAL, TAG_PREMIUM.GOLDEN } : new[] { TAG_PREMIUM.NORMAL })
        {
            string texturePath = def.GetPortraitTexturePath(premium, true, out _);
            if (!string.IsNullOrWhiteSpace(texturePath))
            {
                var preload = Asset<Texture>(texturePath, value => { });
                while (preload.MoveNext()) yield return null;
            }
            else if (!legendary) throw new InvalidOperationException("CardDef has no " + premium + " portrait texture path and no legendary model");
            string materialPath = def.GetPortraitMaterialPath(premium);
            if (!string.IsNullOrWhiteSpace(materialPath))
            {
                var preload = Asset<Material>(materialPath, value => { });
                while (preload.MoveNext()) yield return null;
            }
        }
        string handPath = ActorNames.GetHeroSkinOrHandActor(entity, TAG_PREMIUM.NORMAL);
        foreach (string path in coin ? new[] { handPath } : new[] { handPath, ActorNames.GetPlayActorByTags(entity, TAG_PREMIUM.NORMAL) })
        {
            GameObject actorObject = null;
            var load = Prefab(path, value => actorObject = value);
            while (load.MoveNext()) yield return null;
            stage = (coin ? "coin" : "hero") + " actor binding " + path;
            var actor = actorObject.GetComponent<Actor>();
            if (actor == null) throw new InvalidOperationException("Missing hero Actor: " + path);
            actor.SetEntityDef(entity);
            actor.SetCardDef(cardDef); // Native SetCardDef also creates the legendary portrait for m_LegendaryModel.
            yield return null;
            if (legendary && actor.LegendaryHeroPortrait?.PortraitTexture == null)
                throw new InvalidOperationException("Legendary hero model produced no portrait render texture: " + def.m_LegendaryModel);
            actor.UpdateMaterials(def);
            yield return null;
            while (actor.GetPortraitMaterial() == null || actor.GetPortraitTexture() == null) yield return null;
            details[path == handPath ? "handActor" : "playActor"] = path;
        }
    }

    // Same CardDataModel as CosmeticCoinManager.ShowCoinPreview; bind in an isolated prefab without opening a popup.
    private IEnumerator CoinPreview(CosmeticCoinDbfRecord record)
    {
        stage = "CoinPreview data model";
        GameObject preview = null;
        string path = typeof(CosmeticCoinManager).GetField("COIN_PREVIEW_PREFAB").GetValue(null).ToString();
        details["previewDef"] = "CoinPreview " + path;
        var load = Prefab(path, value => preview = value);
        while (load.MoveNext()) yield return null;
        stage = "CoinPreview data model";
        var coin = preview.GetComponentInChildren<Hearthstone.Progression.CoinPreview>(true)
            ?? throw new InvalidOperationException("Missing CoinPreview component");
        var widget = coin.GetComponent<WidgetTemplate>()
            ?? throw new InvalidOperationException("Missing CoinPreview WidgetTemplate");
        while (!widget.IsReady) yield return null;
        var card = record.CardRecord;
        var model = new Hearthstone.DataModels.CardDataModel {
            CardId = card.NoteMiniGuid, Name = record.Name, FlavorText = card.FlavorText, Premium = TAG_PREMIUM.NORMAL,
            ArtistCredit = string.IsNullOrWhiteSpace(card.ArtistName) ? string.Empty : GameStrings.Format("GLUE_COLLECTION_ARTIST", card.ArtistName)
        };
        widget.BindDataModel(model);
        yield return null;
        if (widget.GetDataModel<Hearthstone.DataModels.CardDataModel>()?.CardId != card.NoteMiniGuid)
            throw new InvalidOperationException("CoinPreview failed to bind its card data model");
        details["previewCardId"] = model.CardId;
    }

    // BaseHeroSkinInfoManager still requires this prefab/component before using the CardDef portrait fallback.
    private IEnumerator CollectionPreview(CardDef def)
    {
        string path = def.m_CollectionHeroDefPath;
        details["previewDef"] = "CollectionHeroDef " + path;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("CardDef has no m_CollectionHeroDefPath");
        GameObject preview = null;
        var load = Prefab(path, value => preview = value);
        while (load.MoveNext()) yield return null;
        if (preview.GetComponent<CollectionHeroDef>() == null)
            throw new InvalidOperationException("Missing CollectionHeroDef component: " + path);
        details["previewSource"] = "Collection CardDef portrait fallback (achievement-exclusive)";
    }

    // Mirrors StoreCollectionManagerCardInfoLoader.TryLoadObjectFromCardInfo for the Shop scene type:
    // m_StoreItemDisplayPath -> StoreItemDisplayDef (m_CustomCMPortraitScene wins, else m_CustomCmPortraitMaterial);
    // otherwise m_CollectionHeroDefPath -> CollectionHeroDef.m_previewMaterial. Either branch may add m_materialAnimationPath.
    private IEnumerator ShopPreview(CardDef def)
    {
        GameObject preview = null;
        bool store = !string.IsNullOrEmpty(def.m_StoreItemDisplayPath);
        string defPath = store ? def.m_StoreItemDisplayPath : def.m_CollectionHeroDefPath;
        string owner = store ? "StoreItemDisplayDef" : "CollectionHeroDef";
        details["previewDef"] = owner + " " + defPath;
        if (string.IsNullOrWhiteSpace(defPath)) throw new InvalidOperationException("CardDef has neither m_StoreItemDisplayPath nor m_CollectionHeroDefPath");
        var load = Prefab(defPath, value => preview = value);
        while (load.MoveNext()) yield return null;
        MaterialReference reference;
        string animation;
        if (store)
        {
            var display = preview.GetComponent<StoreItemDisplayDef>()
                ?? throw new InvalidOperationException("Missing StoreItemDisplayDef component: " + defPath);
            if (!string.IsNullOrEmpty(display.m_CustomCMPortraitScene))
            {
                details["previewSource"] = "StoreItemDisplayDef.m_CustomCMPortraitScene";
                var scene = LegendaryScene(display.m_CustomCMPortraitScene);
                while (scene.MoveNext()) yield return null;
                yield break;
            }
            reference = display.m_CustomCmPortraitMaterial;
            animation = display.m_materialAnimationPath;
            details["previewSource"] = "StoreItemDisplayDef.m_CustomCmPortraitMaterial";
            if (reference == null || string.IsNullOrWhiteSpace(reference.MaterialRef))
                throw new InvalidOperationException("StoreItemDisplayDef has neither m_CustomCMPortraitScene nor m_CustomCmPortraitMaterial (native shop: CODE_CARD_ART_FAILED_TO_LOAD)");
        }
        else
        {
            var hero = preview.GetComponent<CollectionHeroDef>()
                ?? throw new InvalidOperationException("Missing CollectionHeroDef component: " + defPath);
            reference = hero.m_previewMaterial;
            animation = hero.m_materialAnimationPath;
            details["previewSource"] = "CollectionHeroDef.m_previewMaterial";
            if (reference == null || string.IsNullOrWhiteSpace(reference.MaterialRef))
                throw new InvalidOperationException("CollectionHeroDef has no m_previewMaterial path (native shop: CODE_CARD_ART_FAILED_TO_LOAD)");
        }
        // MaterialReference.GetMaterial: material, then m_mainTextureRef only when the material lacks a main texture.
        Material material = null;
        var preload = Asset<Material>(reference.MaterialRef, value => material = value);
        while (preload.MoveNext()) yield return null;
        string texturePath = (string)typeof(MaterialReference).GetField("m_mainTextureRef",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(reference);
        Texture texture = material.mainTexture;
        if (texture == null && !string.IsNullOrWhiteSpace(texturePath))
        {
            preload = Asset<Texture>(texturePath, value => texture = value);
            while (preload.MoveNext()) yield return null;
        }
        if (texture == null) throw new InvalidOperationException("Missing shop/collection portrait texture for " + reference.MaterialRef);
        details["previewMaterial"] = material.name;
        details["previewTexture"] = texture.name;
        if (!string.IsNullOrEmpty(animation))
        {
            preload = Asset<UberShaderAnimation>(animation, value => { });
            while (preload.MoveNext()) yield return null;
            details["previewAnimation"] = animation;
        }
    }

    private IEnumerator CheckPet(LocalShopProduct product)
    {
        stage = "pet DBF mapping";
        if (!NativeUIProbe.PetEntryValid(product)) throw new InvalidOperationException("Pet family/levels differ from pinned catalogue");
        var forms = new JsonList(); details["forms"] = forms;
        foreach (string form in LocalShop.Default.FormsForPet(product.Pet)) {
            stage = "pet CardDef " + form;
            bool received = false; int expected = generation;
            cardDef?.Dispose(); cardDef = null;
            DefLoader.Get().LoadCardDefAsync(form, (id, definition, user) => {
                if (this == null || expected != generation) { definition?.Dispose(); return; }
                cardDef = definition; received = true;
            });
            while (!received) yield return null;
            var definition = cardDef?.CardDef ?? throw new InvalidOperationException("Pet CardDef unavailable offline");
            PetData petData = null; PetVariantData variantData = null;
            var load = Asset<PetData>(definition.m_petDataAssetPath, value => petData = value);
            while (load.MoveNext()) yield return null;
            load = Asset<PetVariantData>(definition.m_petVariantDataAssetPath, value => variantData = value);
            while (load.MoveNext()) yield return null;
            load = Asset<UnityEngine.Object>(petData.UIAnimationOverrideControllerPath, value => { });
            while (load.MoveNext()) yield return null;
            // Warm the actual native UI/gameplay models before SetPet binds the preview's materials and animator.
            foreach (string path in new[] { variantData.FrameModelAssetPath, variantData.SceneModelAssetPath, variantData.GameplayModelAssetPath }) {
                GameObject model = null;
                load = Prefab(path, value => model = value);
                while (load.MoveNext()) yield return null;
                if (!model.GetComponentsInChildren<Renderer>(true).Any(renderer => renderer.sharedMaterials.Any(material => material != null)))
                    throw new InvalidOperationException("Pet model has no renderer/material: " + path);
                if (path == variantData.GameplayModelAssetPath) {
                    var manager = model.GetComponentInChildren<PetModelManager>();
                    if (manager == null || manager.HeroModel == null || manager.DummyModel == null)
                        throw new InvalidOperationException("Gameplay pet model is incomplete: " + path);
                }
            }
            stage = "native pet frame binding " + form;
            var host = new GameObject("SoloStone pet check " + form); instances.Add(host);
            var controller = host.AddComponent<PetControllerFrame>();
            // Prefab deserialization supplies this serialized list in retail. Our isolated host has no
            // external masks; native GetMaskMaterialControllers still unconditionally AddRange's it.
            var externalMasks = typeof(PetControllerUI).GetField("m_externalPetMaskMaterialControllers",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Native pet external-mask binding changed");
            externalMasks.SetValue(controller, new List<PetMaskMaterialController>());
            controller.SetPet(form, true);
            yield return null;
            var bound = controller.PetObject;
            // AnimationModule is runtime-only in the pinned references; inspect the bound native component without adding a DLL pin.
            var animator = bound?.GetComponentsInChildren<Component>(true).FirstOrDefault(component => component.GetType().FullName == "UnityEngine.Animator");
            var animation = animator?.GetType().GetProperty("runtimeAnimatorController")?.GetValue(animator, null) as UnityEngine.Object;
            if (bound == null || animation == null
                || !bound.GetComponentsInChildren<Renderer>(true).Any(renderer => renderer.sharedMaterials.Any(material => material != null)))
                throw new InvalidOperationException("Native pet preview did not bind a model, materials and animator: " + form);
            forms.Add(new JsonNode { { "cardId", form }, { "variantId", NativeUIProbe.PetVariant(form).ID },
                { "frameModel", variantData.FrameModelAssetPath }, { "gameplayModel", variantData.GameplayModelAssetPath },
                { "boundModel", bound.name }, { "animator", animation.name } });
        }
    }

    // BindLegendaryHeroToMaterial.BindMaterialAsync: LegendaryHeroRenderToTextureService.CreatePortrait -> LegendarySkin render texture.
    private IEnumerator LegendaryScene(string path)
    {
        stage = "legendary portrait scene " + path;
        details["previewScene"] = path;
        var warm = Raw(path, value => { });
        while (warm.MoveNext()) yield return null;
        yield return null;
        LegendaryHeroRenderToTextureService service;
        while (!ServiceManager.TryGet(out service)) yield return null;
        // Synchronous native instantiation (the shop does the same); the handle is reference counted by the service.
        var portrait = service.CreatePortrait(path, Player.Side.NEUTRAL)
            ?? throw new InvalidOperationException("Legendary portrait service returned no portrait: " + path);
        assetHandles.Add(portrait);
        var instance = portrait.GetLegendaryHeroPrefabInstance();
        if (instance == null) throw new InvalidOperationException("Legendary portrait scene did not instantiate: " + path);
        if (instance.GetComponentInChildren<LegendarySkin>(true) == null)
            throw new InvalidOperationException("Legendary portrait scene has no LegendarySkin: " + path);
        if (!instance.GetComponentsInChildren<Renderer>(true).Any())
            throw new InvalidOperationException("Legendary portrait scene has no renderer: " + path);
        yield return null;
        var texture = portrait.PortraitTexture;
        if (texture == null) throw new InvalidOperationException("Legendary portrait scene produced no render texture: " + path);
        details["previewTexture"] = texture.name;
    }
}
#endif
