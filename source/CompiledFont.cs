using Fonts.Components;
using FreeType;
using System;
using System.Numerics;
using Textures;
using Unmanaged.Collections;

namespace Rendering.Systems
{
    public readonly struct CompiledFont : IDisposable
    {
        public readonly Face face;
        public readonly AtlasTexture atlas;
        public readonly UnmanagedArray<IsGlyph> glyphs;
        public readonly UnmanagedArray<Vector4> regions;

        public CompiledFont(Face face, AtlasTexture atlas, UnmanagedArray<IsGlyph> glyphs, UnmanagedArray<Vector4> regions)
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