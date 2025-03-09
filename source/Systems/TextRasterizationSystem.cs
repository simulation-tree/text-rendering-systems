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
using Textures;
using Worlds;

namespace TextRendering.Systems
{
    public readonly partial struct TextRasterizationSystem : ISystem
    {
        private readonly Library freeType;
        private readonly Dictionary<Entity, uint> textRequestVersions;
        private readonly Dictionary<Entity, CompiledFont> compiledFonts;
        private readonly Stack<Operation> operations;

        private TextRasterizationSystem(Library freeType, Dictionary<Entity, uint> textRequestVersions, Dictionary<Entity, CompiledFont> compiledFonts, Stack<Operation> operations)
        {
            this.freeType = freeType;
            this.textRequestVersions = textRequestVersions;
            this.compiledFonts = compiledFonts;
            this.operations = operations;
        }

        void ISystem.Start(in SystemContainer systemContainer, in World world)
        {
            if (systemContainer.World == world)
            {
                Library freeType = new();
                Dictionary<Entity, uint> textRequestVersions = new();
                Dictionary<Entity, CompiledFont> compiledFonts = new();
                Stack<Operation> operations = new();
                systemContainer.Write(new TextRasterizationSystem(freeType, textRequestVersions, compiledFonts, operations));
            }
        }

        void ISystem.Update(in SystemContainer systemContainer, in World world, in TimeSpan delta)
        {
            Schema schema = world.Schema;
            GenerateTextMeshes(world, schema, systemContainer.simulator);
            AssignFontAtlases(world, schema);
            PerformOperations(world);
        }

        void ISystem.Finish(in SystemContainer systemContainer, in World world)
        {
            if (systemContainer.World == world)
            {
                while (operations.TryPop(out Operation operation))
                {
                    operation.Dispose();
                }

                operations.Dispose();
                foreach (Entity fontEntity in compiledFonts.Keys)
                {
                    compiledFonts[fontEntity].Dispose();
                }

                compiledFonts.Dispose();
                textRequestVersions.Dispose();
                freeType.Dispose();
            }
        }

        private readonly void AssignFontAtlases(World world, Schema schema)
        {
            ComponentType textRendererType = schema.GetComponentType<IsTextRenderer>();
            ComponentType rendererType = schema.GetComponentType<IsRenderer>();
            foreach (Chunk chunk in world.Chunks)
            {
                Definition definition = chunk.Definition;
                if (definition.ContainsComponent(textRendererType) && !definition.ContainsComponent(rendererType))
                {
                    ReadOnlySpan<uint> entities = chunk.Entities;
                    Span<IsTextRenderer> textRenderers = chunk.GetComponents<IsTextRenderer>(textRendererType);
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

        private readonly void GenerateTextMeshes(World world, Schema schema, Simulator simulator)
        {
            ComponentType textMeshRequestType = schema.GetComponentType<IsTextMeshRequest>();
            foreach (Chunk chunk in world.Chunks)
            {
                Definition definition = chunk.Definition;
                if (definition.ContainsComponent(textMeshRequestType))
                {
                    ReadOnlySpan<uint> entities = chunk.Entities;
                    Span<IsTextMeshRequest> textMeshRequests = chunk.GetComponents<IsTextMeshRequest>(textMeshRequestType);
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

        private readonly void PerformOperations(World world)
        {
            while (operations.TryPop(out Operation operation))
            {
                operation.Perform(world);
                operation.Dispose();
            }
        }

        private readonly bool TryLoad(Entity textMeshEntity, IsTextMeshRequest request, Simulator simulator)
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

                    System.Span<char> text = textMeshEntity.GetArray<TextCharacter>().AsSpan<char>();
                    GenerateTextMesh(ref operation, compiledFont, font, text, pixelSize, simulator);
                    textMeshEntity.TryGetComponent(out IsTextMesh textMeshComponent);
                    operation.AddOrSetComponent(textMeshComponent.IncrementVersion());
                    textMeshEntity.TryGetComponent(out IsMesh meshComponent);
                    operation.AddOrSetComponent(meshComponent.IncrementVersion());
                    operations.Push(operation);
                    return true;
                }
            }

            return false;
        }

        private readonly void GenerateTextMesh(ref Operation operation, CompiledFont compiledFont, Font font, System.Span<char> text, uint pixelSize, Simulator simulator)
        {
            using Array<Vector3> positions = new(text.Length * 4);
            using Array<MeshVertexUV> uvs = new(text.Length * 4);
            using Array<uint> indices = new(text.Length * 6);
            font.GenerateVertices(text, positions.AsSpan());

            int vertexIndex = 0;
            int triangleIndex = 0;
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

                uvs[vertexIndex + 0] = firstUv;
                uvs[vertexIndex + 1] = secondUv;
                uvs[vertexIndex + 2] = thirdUv;
                uvs[vertexIndex + 3] = fourthUv;

                indices[triangleIndex + 0] = (uint)vertexIndex;
                indices[triangleIndex + 1] = (uint)vertexIndex + 1;
                indices[triangleIndex + 2] = (uint)vertexIndex + 2;
                indices[triangleIndex + 3] = (uint)vertexIndex + 2;
                indices[triangleIndex + 4] = (uint)vertexIndex + 3;
                indices[triangleIndex + 5] = (uint)vertexIndex;

                vertexIndex += 4;
                triangleIndex += 6;
            }

            operation.CreateOrSetArray(positions.AsSpan(0, vertexIndex).As<Vector3, MeshVertexPosition>());
            operation.CreateOrSetArray(uvs.AsSpan(0, vertexIndex));
            operation.CreateOrSetArray(indices.AsSpan(0, triangleIndex).As<uint, MeshVertexIndex>());

            using Array<MeshVertexColor> colors = new(text.Length * 4);
            colors.Fill(new(1, 1, 1, 1));
            operation.CreateOrSetArray(colors.AsSpan(0, vertexIndex));
        }

        private readonly bool TryGetOrCompileFont(Font font, int glyphCount, uint pixelSize, Simulator simulator, out CompiledFont compiledFont)
        {
            if (!compiledFonts.TryGetValue(font, out compiledFont))
            {
                World world = font.world;
                LoadData loadMessage = new(world, font.GetComponent<IsFontRequest>().address);
                if (simulator.TryHandleMessage(ref loadMessage) != default)
                {
                    if (loadMessage.IsLoaded)
                    {
                        Face face = freeType.Load(loadMessage.Bytes);
                        loadMessage.Dispose();

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
                            (uint x, uint y) size = bitmap.Size;
                            inputSprites.Add(new(name, (int)size.x, (int)size.y, bitmap.Buffer, Channels.Red));

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
            }

            return !compiledFont.IsDisposed;
        }
    }
}