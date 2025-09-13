using Collections.Generic;
using Data.Messages;
using Fonts;
using Fonts.Components;
using FreeType;
using Materials;
using Materials.Components;
using Meshes;
using Meshes.Components;
using Rendering.Components;
using Simulation;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Textures;
using Unmanaged;
using Worlds;
using Worlds.Messages;

namespace TextRendering.Systems
{
    [SkipLocalsInit]
    public partial class TextMeshGenerationSystem : SystemBase, IListener<Update>
    {
        private readonly World world;
        private readonly Library freeType;
        private readonly Dictionary<uint, uint> textRequestVersions;
        private readonly Dictionary<uint, CompiledFont> compiledFonts;
        private readonly Operation operation;
        private readonly int textRendererType;
        private readonly int rendererType;
        private readonly int textMeshRequestType;
        private readonly int fontRequestType;
        private readonly int glyphArrayType;
        private readonly int glyphComponentType;
        private readonly int meshType;
        private readonly int textMeshType;
        private readonly int textCharacterArrayType;
        private readonly int fontType;
        private readonly int fontMetricsType;

        public TextMeshGenerationSystem(Simulator simulator, World world) : base(simulator)
        {
            this.world = world;
            freeType = new();
            textRequestVersions = new(4);
            compiledFonts = new(4);
            operation = new(world);

            Schema schema = world.Schema;
            textRendererType = schema.GetComponentType<IsTextRenderer>();
            rendererType = schema.GetComponentType<IsRenderer>();
            textMeshRequestType = schema.GetComponentType<IsTextMeshRequest>();
            fontRequestType = schema.GetComponentType<IsFontRequest>();
            glyphArrayType = schema.GetArrayType<FontGlyph>();
            glyphComponentType = schema.GetComponentType<IsGlyph>();
            textMeshType = schema.GetComponentType<IsTextMesh>();
            meshType = schema.GetComponentType<IsMesh>();
            textCharacterArrayType = schema.GetArrayType<TextCharacter>();
            fontType = schema.GetComponentType<IsFont>();
            fontMetricsType = schema.GetComponentType<FontMetrics>();
        }

        public override void Dispose()
        {
            operation.Dispose();
            foreach (CompiledFont compiledFont in compiledFonts.Values)
            {
                compiledFont.Dispose();
            }

            compiledFonts.Dispose();
            textRequestVersions.Dispose();
            freeType.Dispose();
        }


        void IListener<Update>.Receive(ref Update message)
        {
            GenerateTextMeshes();
            AssignFontAtlases();

            if (operation.TryPerform())
            {
                operation.Reset();
            }
        }

        private void AssignFontAtlases()
        {
            ReadOnlySpan<Chunk> chunks = world.Chunks;
            for (int c = 0; c < chunks.Length; c++)
            {
                Chunk chunk = chunks[c];
                if (chunk.componentTypes.Contains(textRendererType) && !chunk.componentTypes.Contains(rendererType))
                {
                    ReadOnlySpan<uint> entities = chunk.Entities;
                    ComponentEnumerator<IsTextRenderer> textRenderers = chunk.GetComponents<IsTextRenderer>(textRendererType);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        ref IsTextRenderer textRenderer = ref textRenderers[i];
                        uint textRendererEntity = entities[i];
                        rint meshReference = textRenderer.textMeshReference;
                        rint materialReference = textRenderer.materialReference;
                        rint fontReference = textRenderer.fontReference;
                        uint materialEntity = world.GetReference(textRendererEntity, materialReference);
                        uint fontEntity = world.GetReference(textRendererEntity, fontReference);
                        CompiledFont compiledFont = compiledFonts[fontEntity];
                        Material material = Entity.Get<Material>(world, materialEntity);
                        operation.SetSelectedEntity(materialEntity);
                        DescriptorResourceKey key = new(0, 0);
                        if (material.TryIndexOfTextureBinding(key, out int index))
                        {
                            TextureBinding binding = material.TextureBindings[index];
                            binding.SetTexture(compiledFont.atlas);
                            operation.SetArrayElement(index, binding);
                        }
                        else
                        {
                            TextureBinding binding = new(0, key, compiledFont.atlas, new(0, 0, 1, 1), TextureFiltering.Linear);
                            int textureBindingCount = world.GetArrayLength<TextureBinding>(materialEntity);
                            textureBindingCount++;
                            operation.ResizeArray<TextureBinding>(textureBindingCount);
                            operation.SetArrayElement(textureBindingCount - 1, binding);
                        }

                        operation.SetSelectedEntity(textRendererEntity);
                        operation.AddComponent(new IsRenderer(meshReference, materialReference, textRenderer.renderMask));
                        Trace.WriteLine($"Assigned font atlas `{compiledFont.atlas}` to text renderer `{textRendererEntity}`");
                    }
                }
            }
        }

        private void GenerateTextMeshes()
        {
            ReadOnlySpan<Chunk> chunks = world.Chunks;
            for (int c = 0; c < chunks.Length; c++)
            {
                Chunk chunk = chunks[c];
                if (chunk.componentTypes.Contains(textMeshRequestType))
                {
                    ReadOnlySpan<uint> entities = chunk.Entities;
                    ComponentEnumerator<IsTextMeshRequest> textMeshRequests = chunk.GetComponents<IsTextMeshRequest>(textMeshRequestType);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        ref IsTextMeshRequest request = ref textMeshRequests[i];
                        if (!request.loaded)
                        {
                            uint textMeshEntity = entities[i];
                            if (TryLoad(textMeshEntity, request))
                            {
                                request.loaded = true;
                            }
                            else
                            {
                                Trace.WriteLine($"Failed to update text mesh `{textMeshEntity}`");
                            }
                        }
                    }
                }
            }
        }

        private bool TryLoad(uint textMeshEntity, IsTextMeshRequest request)
        {
            rint fontReference = request.fontReference;
            uint fontEntity = world.GetReference(textMeshEntity, fontReference);
            Font font = Entity.Get<Font>(world, fontEntity);
            if (font.IsLoaded)
            {
                IsFont component = world.GetComponent<IsFont>(fontEntity, fontType);
                uint pixelSize = component.pixelSize;
                int glyphCount = world.GetArrayLength(fontEntity, glyphArrayType);

                //todo: fault: what if the font changes? this system has no way of knowing when to update the atlases+meshes involved
                if (TryGetOrCompileFont(fontEntity, glyphCount, pixelSize, out CompiledFont compiledFont))
                {
                    operation.SetSelectedEntity(textMeshEntity);
                    world.TryGetComponent(textMeshEntity, meshType, out IsMesh meshComponent);
                    ReadOnlySpan<char> text = world.GetArray<TextCharacter>(textMeshEntity, textCharacterArrayType).AsSpan<char>();
                    GenerateTextMesh(world, fontEntity, compiledFont, pixelSize, text, ref meshComponent);
                    world.TryGetComponent(textMeshEntity, textMeshType, out IsTextMesh textMeshComponent);
                    textMeshComponent.version++;
                    operation.AddOrSetComponent(textMeshComponent);
                    operation.AddOrSetComponent(meshComponent);
                    return true;
                }
            }

            return false;
        }

        private void GenerateTextMesh(World world, uint font, CompiledFont compiledFont, uint pixelSize, ReadOnlySpan<char> text, ref IsMesh meshComponent)
        {
            using Array<Vector3> positions = new(text.Length * 4);
            using Array<MeshVertexUV> uvs = new(text.Length * 4);
            using Array<uint> indices = new(text.Length * 6);
            Values<FontGlyph> glyphs = world.GetArray<FontGlyph>(font, glyphArrayType);
            uint lineHeight = world.GetComponent<FontMetrics>(font, fontMetricsType).lineHeight;
            Font.GenerateVertices(world, font, text, positions.AsSpan(), lineHeight, pixelSize, glyphs);

            meshComponent.vertexCount = 0;
            meshComponent.indexCount = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    continue;
                }
                else if (c == '\r')
                {
                    if (i < text.Length - 1 && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    continue;
                }

                IsGlyph glyph;
                Vector4 region;
                if (c < compiledFont.glyphs.Length)
                {
                    glyph = compiledFont.glyphs[c];
                    region = compiledFont.regions[c];
                }
                else
                {
                    glyph = compiledFont.glyphs['?'];
                    region = compiledFont.regions['?'];
                }

                MeshVertexUV firstUv = new(region.X, region.W);
                MeshVertexUV secondUv = new(region.Z, region.W);
                MeshVertexUV thirdUv = new(region.Z, region.Y);
                MeshVertexUV fourthUv = new(region.X, region.Y);

                uvs[meshComponent.vertexCount + 0] = firstUv;
                uvs[meshComponent.vertexCount + 1] = secondUv;
                uvs[meshComponent.vertexCount + 2] = thirdUv;
                uvs[meshComponent.vertexCount + 3] = fourthUv;

                indices[meshComponent.indexCount + 0] = (uint)meshComponent.vertexCount;
                indices[meshComponent.indexCount + 1] = (uint)meshComponent.vertexCount + 1;
                indices[meshComponent.indexCount + 2] = (uint)meshComponent.vertexCount + 2;
                indices[meshComponent.indexCount + 3] = (uint)meshComponent.vertexCount + 2;
                indices[meshComponent.indexCount + 4] = (uint)meshComponent.vertexCount + 3;
                indices[meshComponent.indexCount + 5] = (uint)meshComponent.vertexCount;

                meshComponent.vertexCount += 4;
                meshComponent.indexCount += 6;
            }

            meshComponent.channels |= MeshChannelMask.Positions | MeshChannelMask.UVs | MeshChannelMask.Colors;
            meshComponent.version++;
            operation.CreateOrSetArray(positions.GetSpan(meshComponent.vertexCount).As<Vector3, MeshVertexPosition>());
            operation.CreateOrSetArray(uvs.GetSpan(meshComponent.vertexCount));
            operation.CreateOrSetArray(indices.GetSpan(meshComponent.indexCount).As<uint, MeshVertexIndex>());

            Span<Vector4> colors = stackalloc Vector4[meshComponent.vertexCount];
            colors.Fill(new Vector4(1, 1, 1, 1));
            operation.CreateOrSetArray(colors.As<Vector4, MeshVertexColor>());
        }

        private bool TryGetOrCompileFont(uint font, int glyphCount, uint pixelSize, out CompiledFont compiledFont)
        {
            if (!compiledFonts.TryGetValue(font, out compiledFont))
            {
                LoadData loadMessage = new(world.GetComponent<IsFontRequest>(font, fontRequestType).address);
                simulator.Broadcast(ref loadMessage);
                if (loadMessage.TryConsume(out ByteReader data))
                {
                    Face face = freeType.Load(data.GetBytes());
                    data.Dispose();

                    face.SetPixelSize(pixelSize, pixelSize);

                    //generate a new texture atlas to be reused
                    using Array<AtlasTexture.InputSprite> inputSprites = new(glyphCount);
                    Span<char> name = stackalloc char[1];
                    Array<IsGlyph> glyphs = new(glyphCount);
                    for (int i = 0; i < glyphCount; i++)
                    {
                        rint glyphReference = world.GetArrayElement<FontGlyph>(font, glyphArrayType, i).value;
                        uint glyphEntity = world.GetReference(font, glyphReference);
                        Glyph glyph = Entity.Get<Glyph>(world, glyphEntity);
                        char character = glyph.Character;
                        name[0] = character;

                        GlyphSlot slot = face.LoadGlyph(face.GetCharIndex(character));
                        Bitmap bitmap = slot.Render();
                        (uint width, uint height) = bitmap.Size;
                        inputSprites[i] = new(name, (int)width, (int)height, bitmap.Buffer, Channels.Red);
                        glyphs[i] = world.GetComponent<IsGlyph>(glyphEntity, glyphComponentType);
                    }

                    AtlasTexture atlas = new(world, inputSprites.AsSpan(), 4);
                    Array<Vector4> regions = new(glyphCount);
                    for (int i = 0; i < glyphCount; i++)
                    {
                        regions[i] = atlas[i].region;
                    }

                    compiledFont = new(face, atlas, glyphs, regions);
                    compiledFonts.Add(font, compiledFont);
                    Trace.WriteLine($"Generated text atlas {compiledFont.atlas} sized {atlas.Dimensions} for font entity `{font}`");
                }
            }

            return !compiledFont.IsDisposed;
        }
    }
}