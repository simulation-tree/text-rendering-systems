using Collections;
using Data.Components;
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
using Unmanaged;
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
            GenerateTextMeshes(world, schema);
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
            ComponentType textRendererType = schema.GetComponent<IsTextRenderer>();
            ComponentType rendererType = schema.GetComponent<IsRenderer>();
            foreach (Chunk chunk in world.Chunks)
            {
                Definition definition = chunk.Definition;
                if (definition.Contains(textRendererType) && !definition.Contains(rendererType))
                {
                    USpan<uint> entities = chunk.Entities;
                    USpan<IsTextRenderer> textRenderers = chunk.GetComponents<IsTextRenderer>(textRendererType);
                    for (uint i = 0; i < entities.Length; i++)
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
                        if (material.TryIndexOfTextureBinding(key, out uint index))
                        {
                            TextureBinding binding = material.TextureBindings[index];
                            binding.SetTexture(compiledFont.atlas);
                            operation.SetArrayElement(index, binding);
                        }
                        else
                        {
                            TextureBinding binding = new(0, key, compiledFont.atlas, new(0, 0, 1, 1), TextureFiltering.Linear);
                            uint textureBindingCount = world.GetArrayLength<TextureBinding>(materialEntity);
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

        private readonly void GenerateTextMeshes(World world, Schema schema)
        {
            ComponentType textMeshRequestType = schema.GetComponent<IsTextMeshRequest>();
            foreach (Chunk chunk in world.Chunks)
            {
                Definition definition = chunk.Definition;
                if (definition.Contains(textMeshRequestType))
                {
                    USpan<uint> entities = chunk.Entities;
                    USpan<IsTextMeshRequest> textMeshRequests = chunk.GetComponents<IsTextMeshRequest>(textMeshRequestType);
                    for (uint i = 0; i < entities.Length; i++)
                    {
                        ref IsTextMeshRequest request = ref textMeshRequests[i];
                        if (!request.loaded)
                        {
                            uint entity = entities[i];
                            Entity textMeshEntity = new(world, entity);
                            Trace.WriteLine($"Generating text mesh for `{textMeshEntity}`");
                            if (TryLoad(textMeshEntity, request, schema))
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

        private readonly bool TryLoad(Entity textMeshEntity, IsTextMeshRequest request, Schema schema)
        {
            World world = textMeshEntity.world;
            rint fontReference = request.fontReference;
            uint fontEntity = textMeshEntity.GetReference(fontReference);
            Font font = new Entity(world, fontEntity).As<Font>();
            if (font.IsLoaded)
            {
                Operation operation = new();
                operation.SelectEntity(textMeshEntity);

                USpan<char> text = textMeshEntity.GetArray<TextCharacter>().As<char>();
                GenerateTextMesh(ref operation, font, text, font.PixelSize);

                textMeshEntity.TryGetComponent(out IsTextMesh textMeshComponent);
                operation.AddOrSetComponent(new IsTextMesh(textMeshComponent.version + 1));
                textMeshEntity.TryGetComponent(out IsMesh meshComponent);
                operation.AddOrSetComponent(new IsMesh(meshComponent.version + 1));
                operations.Push(operation);
                return true;
            }
            else
            {
                return false;
            }
        }

        private readonly unsafe void GenerateTextMesh(ref Operation operation, Font font, USpan<char> text, uint pixelSize)
        {
            Entity fontEntity = font;
            uint glyphCount = fontEntity.GetArrayLength<FontGlyph>();

            //todo: fault: what if the font changes? this system has no way of knowing when to update the atlases+meshes involved
            CompiledFont compiledFont = GetOrCompileFont(fontEntity, glyphCount, pixelSize);

            using Array<Vector3> positions = new(text.Length * 4);
            using Array<MeshVertexUV> uvs = new(text.Length * 4);
            using Array<uint> indices = new(text.Length * 6);
            font.GenerateVertices(text, positions.AsSpan());

            uint vertexIndex = 0;
            uint triangleIndex = 0;
            for (uint i = 0; i < text.Length; i++)
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

                indices[triangleIndex + 0] = vertexIndex;
                indices[triangleIndex + 1] = vertexIndex + 1;
                indices[triangleIndex + 2] = vertexIndex + 2;
                indices[triangleIndex + 3] = vertexIndex + 2;
                indices[triangleIndex + 4] = vertexIndex + 3;
                indices[triangleIndex + 5] = vertexIndex;

                vertexIndex += 4;
                triangleIndex += 6;
            }

            operation.CreateOrSetArray(positions.AsSpan(0, vertexIndex).As<MeshVertexPosition>());
            operation.CreateOrSetArray(uvs.AsSpan(0, vertexIndex));
            operation.CreateOrSetArray(indices.AsSpan(0, triangleIndex).As<MeshVertexIndex>());

            using Array<MeshVertexColor> colors = new(text.Length * 4);
            colors.Fill(new(1, 1, 1, 1));
            operation.CreateOrSetArray(colors.AsSpan(0, vertexIndex));
        }

        private readonly unsafe CompiledFont GetOrCompileFont(Entity fontEntity, uint glyphCount, uint pixelSize)
        {
            if (!compiledFonts.TryGetValue(fontEntity, out CompiledFont compiledFont))
            {
                World world = fontEntity.world;

                //because we know its a Font, we know it was loaded from bytes before so it must have that list
                USpan<BinaryData> bytes = fontEntity.GetArray<BinaryData>();
                Face face = freeType.Load(bytes.Address, bytes.Length);
                face.SetPixelSize(pixelSize, pixelSize);

                //generate a new texture atlas to be reused
                using List<AtlasTexture.InputSprite> inputSprites = new();
                USpan<char> name = stackalloc char[1];
                Array<IsGlyph> glyphs = new(glyphCount);
                for (uint i = 0; i < glyphCount; i++)
                {
                    rint glyphReference = fontEntity.GetArrayElement<FontGlyph>(i).value;
                    uint glyphEntity = fontEntity.GetReference(glyphReference);
                    Glyph glyph = new Entity(world, glyphEntity).As<Glyph>();
                    char character = glyph.Character;
                    name[0] = character;

                    GlyphSlot slot = face.LoadGlyph(face.GetCharIndex(character));
                    Bitmap bitmap = slot.Render();
                    (uint x, uint y) size = bitmap.Size;
                    inputSprites.Add(new(name, size.x, size.y, bitmap.Buffer, Channels.Red));

                    glyphs[i] = world.GetComponent<IsGlyph>(glyphEntity);
                }

                AtlasTexture atlas = new(world, inputSprites.AsSpan(), 4);
                Array<Vector4> regions = new(glyphCount);
                for (uint i = 0; i < glyphCount; i++)
                {
                    regions[i] = atlas[i].region;
                }

                compiledFont = new(face, atlas, glyphs, regions);
                compiledFonts.Add(fontEntity, compiledFont);
                Trace.WriteLine($"Generated text atlas {compiledFont.atlas} sized {atlas.Dimensions} for font entity `{fontEntity}`");
            }

            return compiledFont;
        }
    }
}