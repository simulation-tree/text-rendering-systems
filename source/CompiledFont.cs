using Collections;
using Fonts.Components;
using FreeType;
using System;
using System.Numerics;
using Textures;

namespace TextRendering.Systems
{
    public readonly struct CompiledFont : IDisposable
    {
        public readonly Face face;
        public readonly AtlasTexture atlas;
        public readonly Array<IsGlyph> glyphs;
        public readonly Array<Vector4> regions;

        public CompiledFont(Face face, AtlasTexture atlas, Array<IsGlyph> glyphs, Array<Vector4> regions)
        {
            this.face = face;
            this.atlas = atlas;
            this.glyphs = glyphs;
            this.regions = regions;
        }

        public readonly void Dispose()
        {
            regions.Dispose();
            glyphs.Dispose();
            atlas.Dispose();
            face.Dispose();
        }
    }
}