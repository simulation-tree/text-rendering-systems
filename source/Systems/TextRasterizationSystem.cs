using Collections;
using Data.Components;
using Fonts;
using Fonts.Components;
using FreeType;
using Meshes;
using Meshes.Components;
using Rendering;
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
        private readonly List<Operation> operations;

        private TextRasterizationSystem(Library freeType, Dictionary<Entity, uint> textRequestVersions, Dictionary<Entity, CompiledFont> compiledFonts, List<Operation> operations)
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
                List<Operation> operations = new();
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
                while (operations.Count > 0)
                {
                    Operation operation = operations.RemoveAt(0);
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
            ComponentQuery<IsTextRenderer> textRendererQuery = new(world);
            foreach (var r in textRendererQuery)
            {
                ref IsTextRenderer textRenderer = ref r.component1;
                uint textRendererEntity = r.entity;
                rint meshReference = textRenderer.textMeshReference;
                if (!world.ContainsComponent<IsRenderer>(textRendererEntity))
                {
                    rint materialReference = textRenderer.materialReference;
                    rint fontReference = textRenderer.fontReference;
                    uint materialEntity = world.GetReference(textRendererEntity, materialReference);
                    uint fontEntity = world.GetReference(textRendererEntity, fontReference);
                    Entity font = new(world, fontEntity);
                    CompiledFont compiledFont = compiledFonts[font];
                    Material material = new(world, materialEntity);
                    Operation operation = new();
                    Operation.SelectedEntity selectedEntity = operation.SelectEntity(materialEntity);
                    if (material.TryGetTextureBinding(0, 0, out uint index))
                    {
                        MaterialTextureBinding binding = world.GetArrayElement<MaterialTextureBinding>(materialEntity, index);
                        binding.SetTexture(compiledFont.atlas);
                        selectedEntity.SetArrayElement(index, binding, schema);
                    }
                    else
                    {
                        MaterialTextureBinding binding = new(0, new(0, 0), compiledFont.atlas, new(0, 0, 1, 1), TextureFiltering.Linear);
                        uint textureBindingCount = world.GetArrayLength<MaterialTextureBinding>(materialEntity);
                        textureBindingCount++;
                        selectedEntity.ResizeArray<MaterialTextureBinding>(textureBindingCount, schema);
                        selectedEntity.SetArrayElement(textureBindingCount - 1, binding, schema);
                    }

                    operation.ClearSelection();
                    selectedEntity = operation.SelectEntity(textRendererEntity);
                    selectedEntity.AddComponent(new IsRenderer(meshReference, materialReference, textRenderer.mask), schema);
                    operations.Add(operation);
                    Trace.WriteLine($"Assigned font atlas `{compiledFont.atlas}` to text renderer `{textRendererEntity}`");
                }
            }
        }

        private readonly void GenerateTextMeshes(World world, Schema schema)
        {
            ComponentQuery<IsTextMeshRequest> textMeshRequestQuery = new(world);
            foreach (var r in textMeshRequestQuery)
            {
                ref IsTextMeshRequest request = ref r.component1;
                bool sourceChanged;
                Entity textMeshEntity = new(world, r.entity);
                if (!textRequestVersions.ContainsKey(textMeshEntity))
                {
                    sourceChanged = true;
                }
                else
                {
                    sourceChanged = textRequestVersions[textMeshEntity] != request.version;
                }

                if (sourceChanged)
                {
                    Trace.WriteLine($"Generating text mesh for `{textMeshEntity}`");
                    if (TryLoad(textMeshEntity, request, schema))
                    {
                        textRequestVersions.AddOrSet(textMeshEntity, request.version);
                    }
                    else
                    {
                        Trace.WriteLine($"Failed to update text mesh `{textMeshEntity}`");
                    }
                }
            }
        }

        private readonly void PerformOperations(World world)
        {
            while (operations.Count > 0)
            {
                Operation operation = operations.RemoveAt(0);
                world.Perform(operation);
                operation.Dispose();
            }
        }

        private readonly bool TryLoad(Entity textMeshEntity, IsTextMeshRequest request, Schema schema)
        {
            World world = textMeshEntity.GetWorld();
            rint fontReference = request.fontReference;
            uint fontEntity = textMeshEntity.GetReference(fontReference);
            Font font = new(world, fontEntity);
            if (font.Is())
            {
                Operation operation = new();
                Operation.SelectedEntity selectedEntity = operation.SelectEntity(textMeshEntity);

                //reset the mesh
                if (!textMeshEntity.ContainsArray<MeshVertexPosition>())
                {
                    selectedEntity.CreateArray<MeshVertexPosition>(0, schema);
                }

                if (!textMeshEntity.ContainsArray<MeshVertexIndex>())
                {
                    selectedEntity.CreateArray<MeshVertexIndex>(0, schema);
                }

                if (!textMeshEntity.ContainsArray<MeshVertexUV>())
                {
                    selectedEntity.CreateArray<MeshVertexUV>(0, schema);
                }

                if (textMeshEntity.ContainsArray<MeshVertexColor>())
                {
                    selectedEntity.DestroyArray<MeshVertexColor>(schema);
                }

                USpan<char> text = textMeshEntity.GetArray<TextCharacter>().As<char>();
                GenerateTextMesh(ref selectedEntity, font, text, font.PixelSize, schema);

                //update proof components to fulfil the type argument
                ref IsTextMesh textMeshProof = ref textMeshEntity.TryGetComponent<IsTextMesh>(out bool contains);
                if (contains)
                {
                    textMeshProof.version++;
                    selectedEntity.SetComponent(new IsTextMesh(textMeshProof.version + 1), schema);
                }
                else
                {
                    selectedEntity.AddComponent(new IsTextMesh(), schema);
                }

                ref IsMesh meshProof = ref textMeshEntity.TryGetComponent<IsMesh>(out contains);
                if (contains)
                {
                    meshProof.version++;
                    selectedEntity.SetComponent(new IsMesh(meshProof.version + 1), schema);
                }
                else
                {
                    selectedEntity.AddComponent(new IsMesh(), schema);
                }

                operations.Add(operation);
                return true;
            }
            else
            {
                return false;
            }
        }

        private readonly unsafe void GenerateTextMesh(ref Operation.SelectedEntity selectedEntity, Font font, USpan<char> text, uint pixelSize, Schema schema)
        {
            Entity fontEntity = font;
            uint glyphCount = fontEntity.GetArrayLength<FontGlyph>();

            //todo: fault: what if the font changes? this system has no way of knowing when to update the atlases+meshes involved
            CompiledFont compiledFont = GetOrCompileFont(fontEntity, glyphCount, pixelSize);

            using Array<MeshVertexPosition> positions = new(text.Length * 4);
            using Array<MeshVertexUV> uvs = new(text.Length * 4);
            using Array<uint> indices = new(text.Length * 6);
            USpan<Vector3> vertices = positions.AsSpan().As<Vector3>();
            (Vector2 maxPosition, uint vertexCount) = font.GenerateVertices(text, vertices);

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

                IsGlyph glyph = compiledFont.glyphs[c];
                Vector4 region = compiledFont.regions[c];
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

            selectedEntity.ResizeArray<MeshVertexPosition>(vertexIndex, schema);
            selectedEntity.SetArrayElements(0, positions.AsSpan(0, vertexIndex), schema);
            selectedEntity.ResizeArray<MeshVertexUV>(vertexIndex, schema);
            selectedEntity.SetArrayElements(0, uvs.AsSpan(0, vertexIndex), schema);
            selectedEntity.ResizeArray<MeshVertexIndex>(triangleIndex, schema);
            selectedEntity.SetArrayElements(0, indices.AsSpan(0, triangleIndex).As<MeshVertexIndex>(), schema);
        }

        private readonly unsafe CompiledFont GetOrCompileFont(Entity fontEntity, uint glyphCount, uint pixelSize)
        {
            if (!compiledFonts.TryGetValue(fontEntity, out CompiledFont compiledFont))
            {
                World world = fontEntity.GetWorld();

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
                    Glyph glyph = new(world, glyphEntity);
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
                Trace.WriteLine($"Generated text atlas {compiledFont.atlas} sized {atlas.Size} for font entity `{fontEntity}`");
            }

            return compiledFont;
        }
    }
}