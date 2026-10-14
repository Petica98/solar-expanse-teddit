using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;

namespace Teddit
{
    /// <summary>
    /// What a custom icon is used for. Each kind of icon is sized and spaced
    /// differently by the base game when it appears inline in TMP text.
    /// </summary>
    internal enum IconKind
    {
        Facility,
        Vehicle,
        Research,
        Resource,
    }

    /// <summary>
    /// Optional per-icon tuning, read from an entry's iconLayout block:
    ///   iconLayout:
    ///     scale: 1.0     multiplier on the vanilla size for this icon kind
    ///     offsetY: 0.0   move the icon up (+) or down (-), as a fraction of its height
    ///     spacing: 0.0   extra gap after the icon, as a fraction of its height
    /// </summary>
    internal sealed class IconLayout
    {
        public float Scale = 1f;
        public float OffsetY = 0f;
        public float Spacing = 0f;

        public static readonly IconLayout Default = new IconLayout();

        public static IconLayout FromDef(Dictionary<string, JToken> def)
        {
            JToken tok;
            if (def == null || !def.TryGetValue("iconLayout", out tok) || !(tok is JObject obj))
                return Default;

            var layout = new IconLayout();
            try
            {
                layout.Scale   = obj.Value<float?>("scale")   ?? 1f;
                layout.OffsetY = obj.Value<float?>("offsetY") ?? 0f;
                layout.Spacing = obj.Value<float?>("spacing") ?? 0f;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[IconLayout] Invalid iconLayout block: {ex.Message}");
                return Default;
            }
            if (layout.Scale <= 0f) layout.Scale = 1f;
            return layout;
        }
    }

    /// <summary>
    /// Builds TMP glyph metrics for custom icons so they match the base game's
    /// icons of the same kind.
    ///
    /// The game's sprite assets have faceInfo.pointSize == 0, so TMP draws a sprite
    /// at height = font ascent * character.scale * glyph.scale, and positions it
    /// with horizontalBearingX/Y and horizontalAdvance relative to metrics.height.
    /// The vanilla values differ per kind (resources 1.5x with no side gap, research
    /// 2.0x with gaps on both sides, vehicles 1.7x), so one donor glyph can't serve
    /// every kind.
    /// </summary>
    internal static class IconMetrics
    {
        struct Reference
        {
            public string AssetName;
            public GlyphMetrics Metrics;
            public float Scale;
        }

        // Values read from the game's own sprite assets (game version of Sept 2026).
        // Used only when the live asset can't be found.
        static readonly Dictionary<IconKind, Reference> _fallbacks = new Dictionary<IconKind, Reference>
        {
            { IconKind.Facility, new Reference { AssetName = "build_modular_habitat", Metrics = new GlyphMetrics(64f, 64f, 0f, 50f, 64f), Scale = 2.0f } },
            { IconKind.Vehicle,  new Reference { AssetName = "hermes",                Metrics = new GlyphMetrics(64f, 64f, 0f, 50f, 66f), Scale = 1.7f } },
            { IconKind.Research, new Reference { AssetName = "ResearchIconsAtlas",    Metrics = new GlyphMetrics(64f, 64f, 10.1f, 44f, 86f), Scale = 2.0f } },
            { IconKind.Resource, new Reference { AssetName = "solar_expanse_icons",   Metrics = new GlyphMetrics(256f, 256f, 0f, 190f, 256f), Scale = 1.5f } },
        };

        static readonly Dictionary<IconKind, Reference> _cache = new Dictionary<IconKind, Reference>();

        /// <summary>
        /// Creates a sprite glyph for a texture so it renders like a vanilla icon of
        /// the given kind. Non-square textures keep their aspect ratio.
        /// </summary>
        public static TMP_SpriteGlyph BuildGlyph(Texture2D tex, IconKind kind, IconLayout layout)
        {
            var reference = GetReference(kind);
            var m = reference.Metrics;
            layout = layout ?? IconLayout.Default;

            float height   = m.height;
            float width    = tex.height > 0 ? height * tex.width / tex.height : height;
            float trailing = m.horizontalAdvance - m.horizontalBearingX - m.width;

            var glyph = new TMP_SpriteGlyph();
            glyph.index      = 0;
            glyph.metrics    = new GlyphMetrics(
                width,
                height,
                m.horizontalBearingX,
                m.horizontalBearingY + layout.OffsetY * height,
                m.horizontalBearingX + width + trailing + layout.Spacing * height);
            glyph.glyphRect  = new GlyphRect(0, 0, tex.width, tex.height);
            glyph.scale      = reference.Scale * layout.Scale;
            glyph.atlasIndex = 0;
            return glyph;
        }

        static Reference GetReference(IconKind kind)
        {
            Reference cached;
            if (_cache.TryGetValue(kind, out cached))
                return cached;

            var fallback = _fallbacks[kind];
            var live = FindLiveGlyph(fallback.AssetName);
            if (live == null)
            {
                Plugin.Log.LogDebug($"[IconMetrics] '{fallback.AssetName}' not loaded — using built-in {kind} icon metrics.");
                return fallback;
            }

            var reference = new Reference { AssetName = fallback.AssetName, Metrics = live.metrics, Scale = live.scale };
            _cache[kind] = reference;
            return reference;
        }

        static TMP_SpriteGlyph FindLiveGlyph(string assetName)
        {
            foreach (var asset in Resources.FindObjectsOfTypeAll<TMP_SpriteAsset>())
            {
                if (asset == null || asset.name != assetName || asset.spriteGlyphTable == null)
                    continue;
                foreach (var glyph in asset.spriteGlyphTable)
                    if (glyph != null && glyph.metrics.height > 0f && glyph.metrics.horizontalAdvance > 0f)
                        return glyph;
            }
            return null;
        }

        /// <summary>
        /// Returns a copy of the texture with a full mipmap chain. Icons are drawn at
        /// ~30-40 px, and without mipmaps a larger source image renders jagged.
        /// </summary>
        public static Texture2D WithMipmaps(Texture2D src)
        {
            if (src == null || src.mipmapCount > 1)
                return src;
            try
            {
                var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, mipChain: true);
                tex.name = src.name;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.SetPixels32(src.GetPixels32());
                tex.Apply(updateMipmaps: true, makeNoLongerReadable: false);
                return tex;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[IconMetrics] Could not build mipmaps for {src.name}: {ex.Message}");
                return src;
            }
        }
    }
}
