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

namespace TextRendering.Systems
{
    [SkipLocalsInit]
    public class TextMeshGenerationSystem : ISystem, IDisposable
    {
        private readonly Library freeType;
        private readonly Dictionary<Entity, uint> textRequestVersions;
        private readonly Dictionary<Entity, CompiledFont> compiledFonts;
        private readonly Stack<Operation> operations;

        public TextMeshGenerationSystem()
        {
            freeType = new();
            textRequestVersions = new(4);
            compiledFonts = new(4);
            operations = new();
        }

        public void Dispose()
        {
            while (operations.TryPop(out Operation operation))
            {
                operation.Dispose();
            }

            operations.Dispose();
            foreach (CompiledFont compiledFont in compiledFonts.Values)
            {
                compiledFont.Dispose();
            }

            compiledFonts.Dispose();
            textRequestVersions.Dispose();
            freeType.Dispose();
        }

        void ISystem.Update(Simulator simulator, double deltaTime)
        {
            World world = simulator.world;
            Schema schema = world.Schema;
            GenerateTextMeshes(simulator, schema);
            AssignFontAtlases(world, schema);
            PerformOperations(world);
        }

        private void AssignFontAtlases(World world, Schema schema)
        {
            int textRendererType = schema.GetComponentType<IsTextRenderer>();
            int rendererType = schema.GetComponentType<IsRenderer>();
            foreach (Chunk chunk in world.Chunks)
            {
                Definition definition = chunk.Definition;
                if (definition.ContainsComponent(textRendererType) && !definition.ContainsComponent(rendererType))
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
                        Entity font = new(world, fontEntity);
                        CompiledFont compiledFont = compiledFonts[font];
                        Material material = new Entity(world, materialEntity).As<Material>();
                        Operation operation = new();
                        operation.SelectEntity(materialEntity);
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

                        operation.ClearSelection();
                        operation.SelectEntity(textRendererEntity);
                        operation.AddComponent(new IsRenderer(meshReference, materialReference, textRenderer.renderMask));
                        operations.Push(operation);
                        Trace.WriteLine($"Assigned font atlas `{compiledFont.atlas}` to text renderer `{textRendererEntity}`");
                    }
                }
            }
        }

        private void GenerateTextMeshes(Simulator simulator, Schema schema)
        {
            World world = simulator.world;
            int textMeshRequestType = schema.GetComponentType<IsTextMeshRequest>();
            foreach (Chunk chunk in world.Chunks)
            {
                Definition definition = chunk.Definition;
                if (definition.ContainsComponent(textMeshRequestType))
                {
                    ReadOnlySpan<uint> entities = chunk.Entities;
                    ComponentEnumerator<IsTextMeshRequest> textMeshRequests = chunk.GetComponents<IsTextMeshRequest>(textMeshRequestType);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        ref IsTextMeshRequest request = ref textMeshRequests[i];
                        if (!request.loaded)
                        {
                            uint entity = entities[i];
                            Entity textMeshEntity = new(world, entity);
                            if (TryLoad(textMeshEntity, request, simulator))
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

        private void PerformOperations(World world)
        {
            while (operations.TryPop(out Operation operation))
            {
                operation.Perform(world);
                operation.Dispose();
            }
        }

        private bool TryLoad(Entity textMeshEntity, IsTextMeshRequest request, Simulator simulator)
        {
            World world = textMeshEntity.world;
            rint fontReference = request.fontReference;
            uint fontEntity = textMeshEntity.GetReference(fontReference);
            Font font = new Entity(world, fontEntity).As<Font>();
            if (font.IsLoaded)
            {
                uint pixelSize = font.PixelSize;
                int glyphCount = font.GetArrayLength<FontGlyph>();

                //todo: fault: what if the font changes? this system has no way of knowing when to update the atlases+meshes involved
                if (TryGetOrCompileFont(font, glyphCount, pixelSize, simulator, out CompiledFont compiledFont))
                {
                    Operation operation = new();
                    operation.SelectEntity(textMeshEntity);

                    textMeshEntity.TryGetComponent(out IsMesh meshComponent);
                    ReadOnlySpan<char> text = textMeshEntity.GetArray<TextCharacter>().AsSpan<char>();
                    GenerateTextMesh(ref operation, compiledFont, font, text, ref meshComponent);
                    textMeshEntity.TryGetComponent(out IsTextMesh textMeshComponent);
                    operation.AddOrSetComponent(textMeshComponent.IncrementVersion());
                    operation.AddOrSetComponent(meshComponent);
                    operations.Push(operation);
                    return true;
                }
            }

            return false;
        }

        private static void GenerateTextMesh(ref Operation operation, CompiledFont compiledFont, Font font, ReadOnlySpan<char> text, ref IsMesh meshComponent)
        {
            using Array<Vector3> positions = new(text.Length * 4);
            using Array<MeshVertexUV> uvs = new(text.Length * 4);
            using Array<uint> indices = new(text.Length * 6);
            font.GenerateVertices(text, positions.AsSpan());

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

        private bool TryGetOrCompileFont(Font font, int glyphCount, uint pixelSize, Simulator simulator, out CompiledFont compiledFont)
        {
            if (!compiledFonts.TryGetValue(font, out compiledFont))
            {
                World world = font.world;
                LoadData loadMessage = new(world, font.GetComponent<IsFontRequest>().address);
                simulator.Broadcast(ref loadMessage);
                if (loadMessage.TryConsume(out ByteReader data))
                {
                    Face face = freeType.Load(data.GetBytes());
                    data.Dispose();

                    face.SetPixelSize(pixelSize, pixelSize);

                    //generate a new texture atlas to be reused
                    using List<AtlasTexture.InputSprite> inputSprites = new();
                    Span<char> name = stackalloc char[1];
                    Array<IsGlyph> glyphs = new(glyphCount);
                    for (int i = 0; i < glyphCount; i++)
                    {
                        rint glyphReference = font.GetArrayElement<FontGlyph>(i).value;
                        uint glyphEntity = font.GetReference(glyphReference);
                        Glyph glyph = new Entity(world, glyphEntity).As<Glyph>();
                        char character = glyph.Character;
                        name[0] = character;

                        GlyphSlot slot = face.LoadGlyph(face.GetCharIndex(character));
                        Bitmap bitmap = slot.Render();
                        (uint width, uint height) = bitmap.Size;
                        inputSprites.Add(new(name, (int)width, (int)height, bitmap.Buffer, Channels.Red));

                        glyphs[i] = world.GetComponent<IsGlyph>(glyphEntity);
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