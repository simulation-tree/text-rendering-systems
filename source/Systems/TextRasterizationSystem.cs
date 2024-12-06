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

        public TextRasterizationSystem()
        {
            freeType = new();
            textRequestVersions = new();
            compiledFonts = new();
            operations = new();
        }

        void ISystem.Start(in SystemContainer systemContainer, in World world)
        {
        }

        void ISystem.Update(in SystemContainer systemContainer, in World world, in TimeSpan delta)
        {
            GenerateTextMeshes(world);
            AssignFontAtlases(world);
            PerformOperations(world);
        }

        void ISystem.Finish(in SystemContainer systemContainer, in World world)
        {
        }

        void IDisposable.Dispose()
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

        private readonly void AssignFontAtlases(World world)
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
                        selectedEntity.SetArrayElement(index, binding);
                    }
                    else
                    {
                        MaterialTextureBinding binding = new(0, new(0, 0), compiledFont.atlas, new(0, 0, 1, 1), TextureFiltering.Linear);
                        uint textureBindingCount = world.GetArrayLength<MaterialTextureBinding>(materialEntity);
                        textureBindingCount++;
                        selectedEntity.ResizeArray<MaterialTextureBinding>(textureBindingCount);
                        selectedEntity.SetArrayElement(textureBindingCount - 1, binding);
                    }

                    operation.ClearSelection();
                    selectedEntity = operation.SelectEntity(textRendererEntity);
                    selectedEntity.AddComponent(new IsRenderer(meshReference, materialReference, textRenderer.mask));
                    operations.Add(operation);
                    Trace.WriteLine($"Assigned font atlas `{compiledFont.atlas}` to text renderer `{textRendererEntity}`");
                }
            }
        }

        private readonly void GenerateTextMeshes(World world)
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
                    if (TryLoad(textMeshEntity, request))
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

        private readonly bool TryLoad(Entity textMeshEntity, IsTextMeshRequest request)
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
                    selectedEntity.CreateArray<MeshVertexPosition>(0);
                }

                if (!textMeshEntity.ContainsArray<MeshVertexIndex>())
                {
                    selectedEntity.CreateArray<MeshVertexIndex>(0);
                }

                if (!textMeshEntity.ContainsArray<MeshVertexUV>())
                {
                    selectedEntity.CreateArray<MeshVertexUV>(0);
                }

                if (textMeshEntity.ContainsArray<MeshVertexColor>())
                {
                    selectedEntity.DestroyArray<MeshVertexColor>();
                }

                USpan<char> text = textMeshEntity.GetArray<TextCharacter>().As<char>();
                GenerateTextMesh(ref selectedEntity, font, text);

                //update proof components to fulfil the type argument
                ref IsTextMesh textMeshProof = ref textMeshEntity.TryGetComponent<IsTextMesh>(out bool contains);
                if (contains)
                {
                    textMeshProof.version++;
                    selectedEntity.SetComponent(new IsTextMesh(textMeshProof.version + 1));
                }
                else
                {
                    selectedEntity.AddComponent(new IsTextMesh());
                }

                ref IsMesh meshProof = ref textMeshEntity.TryGetComponent<IsMesh>(out contains);
                if (contains)
                {
                    meshProof.version++;
                    selectedEntity.SetComponent(new IsMesh(meshProof.version + 1));
                }
                else
                {
                    selectedEntity.AddComponent(new IsMesh());
                }

                operations.Add(operation);
                return true;
            }
            else
            {
                return false;
            }
        }

        private readonly unsafe void GenerateTextMesh(ref Operation.SelectedEntity selectedEntity, Font font, USpan<char> text)
        {
            Entity fontEntity = font;
            uint glyphCount = fontEntity.GetArrayLength<FontGlyph>();
            uint pixelSize = 32;

            //todo: fault: what if the font changes? this system has no way of knowing when to update the atlases+meshes involved
            CompiledFont compiledFont = GetOrCompileFont(fontEntity, glyphCount, pixelSize);

            using Array<MeshVertexPosition> positions = new(text.Length * 4);
            using Array<MeshVertexUV> uvs = new(text.Length * 4);
            using Array<uint> indices = new(text.Length * 6);
            USpan<Vector3> vertices = positions.AsSpan().As<Vector3>();
            Vector2 maxPosition = font.GenerateVertices(text, vertices, pixelSize);

            uint vertexIndex = 0;
            for (uint i = 0; i < text.Length; i++)
            {
                char c = text[i];
                IsGlyph glyph = compiledFont.glyphs[c];
                if (c == '\n')
                {
                    continue;
                }

                Vector4 region = compiledFont.regions[c];
                MeshVertexUV firstUv = new(region.X, region.W);
                MeshVertexUV secondUv = new(region.Z, region.W);
                MeshVertexUV thirdUv = new(region.Z, region.Y);
                MeshVertexUV fourthUv = new(region.X, region.Y);

                uvs[i * 4 + 0] = firstUv;
                uvs[i * 4 + 1] = secondUv;
                uvs[i * 4 + 2] = thirdUv;
                uvs[i * 4 + 3] = fourthUv;

                indices[i * 6 + 0] = vertexIndex;
                indices[i * 6 + 1] = vertexIndex + 1;
                indices[i * 6 + 2] = vertexIndex + 2;
                indices[i * 6 + 3] = vertexIndex + 2;
                indices[i * 6 + 4] = vertexIndex + 3;
                indices[i * 6 + 5] = vertexIndex;
                vertexIndex += 4;
            }

            selectedEntity.ResizeArray<MeshVertexPosition>(positions.Length);
            selectedEntity.SetArrayElements(0, positions.AsSpan());
            selectedEntity.ResizeArray<MeshVertexUV>(uvs.Length);
            selectedEntity.SetArrayElements(0, uvs.AsSpan());
            selectedEntity.ResizeArray<MeshVertexIndex>(indices.Length);
            selectedEntity.SetArrayElements(0, indices.AsSpan().As<MeshVertexIndex>());
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