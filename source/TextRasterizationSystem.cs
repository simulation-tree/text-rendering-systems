using Collections;
using Fonts;
using Fonts.Components;
using FreeType;
using Meshes;
using Meshes.Components;
using Rendering.Components;
using Simulation;
using Simulation.Functions;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Textures;
using Unmanaged;

namespace Rendering.Systems
{
    public readonly struct TextRasterizationSystem : ISystem
    {
        private readonly Library freeType;
        private readonly ComponentQuery<IsTextMeshRequest> textQuery;
        private readonly ComponentQuery<IsTextRenderer> textRendererQuery;
        private readonly Dictionary<Entity, uint> textRequestVersions;
        private readonly Dictionary<Entity, CompiledFont> compiledFonts;
        private readonly List<Operation> operations;

        readonly unsafe InitializeFunction ISystem.Initialize => new(&Initialize);
        readonly unsafe IterateFunction ISystem.Iterate => new(&Update);
        readonly unsafe FinalizeFunction ISystem.Finalize => new(&Finalize);

        [UnmanagedCallersOnly]
        private static void Initialize(SystemContainer container, World world)
        {
        }

        [UnmanagedCallersOnly]
        private static void Update(SystemContainer container, World world, TimeSpan delta)
        {
            ref TextRasterizationSystem system = ref container.Read<TextRasterizationSystem>();
            system.Update(world);
        }

        [UnmanagedCallersOnly]
        private static void Finalize(SystemContainer container, World world)
        {
            if (container.World == world)
            {
                ref TextRasterizationSystem system = ref container.Read<TextRasterizationSystem>();
                system.CleanUp();
            }
        }

        public TextRasterizationSystem()
        {
            freeType = new();
            textQuery = new();
            textRendererQuery = new();
            textRequestVersions = new();
            compiledFonts = new();
            operations = new();
        }

        private void CleanUp()
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
            textRendererQuery.Dispose();
            textQuery.Dispose();
            freeType.Dispose();
        }

        private void Update(World world)
        {
            UpdateTextMeshes(world);
            UpdateTextRendererMaterials(world);
            PerformOperations(world);
        }

        private void UpdateTextMeshes(World world)
        {
            textQuery.Update(world);
            foreach (var x in textQuery)
            {
                IsTextMeshRequest request = x.Component1;
                bool sourceChanged = false;
                Entity textMeshEntity = new(world, x.entity);
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
                    if (TryUpdateTextMesh((textMeshEntity, request)))
                    {
                        textRequestVersions.AddOrSet(textMeshEntity, request.version);
                    }
                    else
                    {
                        Debug.WriteLine("Failed to update text mesh.");
                    }
                }
            }
        }

        private void UpdateTextRendererMaterials(World world)
        {
            textRendererQuery.Update(world);
            foreach (var x in textRendererQuery)
            {
                IsTextRenderer textRenderer = x.Component1;
                uint textRendererEntity = x.entity;
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
                    operation.SelectEntity(materialEntity);
                    if (material.TryGetTextureBinding(0, 0, out uint index))
                    {
                        MaterialTextureBinding binding = world.GetArrayElementRef<MaterialTextureBinding>(materialEntity, index);
                        binding.SetTexture(compiledFont.atlas.texture);
                        operation.SetArrayElement(index, binding);
                    }
                    else
                    {
                        MaterialTextureBinding binding = new(0, new(0, 0), compiledFont.atlas.texture, new(0, 0, 1, 1), TextureFiltering.Linear);
                        uint textureBindingCount = world.GetArrayLength<MaterialTextureBinding>(materialEntity);
                        textureBindingCount++;
                        operation.ResizeArray<MaterialTextureBinding>(textureBindingCount);
                        operation.SetArrayElement(textureBindingCount - 1, binding);
                    }

                    operation.ClearSelection();
                    operation.SelectEntity(textRendererEntity);

                    rint cameraReference = textRenderer.cameraReference;
                    operation.AddComponent(new IsRenderer(meshReference, materialReference, cameraReference));
                    operations.Add(operation);
                }
            }
        }

        private void PerformOperations(World world)
        {
            while (operations.Count > 0)
            {
                Operation operation = operations.RemoveAt(0);
                world.Perform(operation);
                operation.Dispose();
            }
        }

        private bool TryUpdateTextMesh((Entity textMeshEntity, IsTextMeshRequest request) input)
        {
            Entity textMeshEntity = input.textMeshEntity;
            World world = textMeshEntity.GetWorld();
            rint fontReference = input.request.fontReference;
            uint fontEntity = textMeshEntity.GetReference(fontReference);
            Font font = new(world, fontEntity);
            if (font.IsCompliant())
            {
                Operation operation = new();
                operation.SelectEntity(textMeshEntity);

                //reset the mesh
                if (!textMeshEntity.ContainsArray<MeshVertexPosition>())
                {
                    operation.CreateArray<MeshVertexPosition>(0);
                }

                if (!textMeshEntity.ContainsArray<uint>())
                {
                    operation.CreateArray<uint>(0);
                }

                if (!textMeshEntity.ContainsArray<MeshVertexUV>())
                {
                    operation.CreateArray<MeshVertexUV>(0);
                }

                if (textMeshEntity.ContainsArray<MeshVertexColor>())
                {
                    operation.DestroyArray<MeshVertexColor>();
                }

                USpan<char> text = textMeshEntity.GetArray<char>();
                GenerateTextMesh(ref operation, font, text);

                //update proof components to fulfil the type argument
                if (textMeshEntity.TryGetComponent(out IsTextMesh textMeshProof))
                {
                    textMeshProof.version++;
                    operation.SetComponent(textMeshProof);
                }
                else
                {
                    operation.AddComponent(new IsTextMesh());
                }

                if (textMeshEntity.TryGetComponent(out IsMesh meshProof))
                {
                    meshProof.version++;
                    operation.SetComponent(meshProof);
                }
                else
                {
                    operation.AddComponent(new IsMesh());
                }

                operations.Add(operation);
                return true;
            }
            else
            {
                return false;
            }
        }

        private unsafe void GenerateTextMesh(ref Operation operation, Font font, USpan<char> text)
        {
            Entity fontEntity = font.entity;
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

                uvs[(i * 4) + 0] = firstUv;
                uvs[(i * 4) + 1] = secondUv;
                uvs[(i * 4) + 2] = thirdUv;
                uvs[(i * 4) + 3] = fourthUv;

                indices[(i * 6) + 0] = vertexIndex;
                indices[(i * 6) + 1] = vertexIndex + 1;
                indices[(i * 6) + 2] = vertexIndex + 2;
                indices[(i * 6) + 3] = vertexIndex + 2;
                indices[(i * 6) + 4] = vertexIndex + 3;
                indices[(i * 6) + 5] = vertexIndex;
                vertexIndex += 4;
            }

            operation.ResizeArray<MeshVertexPosition>(positions.Length);
            operation.SetArrayElements(0, positions.AsSpan());
            operation.ResizeArray<MeshVertexUV>(uvs.Length);
            operation.SetArrayElements(0, uvs.AsSpan());
            operation.ResizeArray<uint>(indices.Length);
            operation.SetArrayElements(0, indices.AsSpan());
        }

        private unsafe CompiledFont GetOrCompileFont(Entity fontEntity, uint glyphCount, uint pixelSize)
        {
            if (!compiledFonts.TryGetValue(fontEntity, out CompiledFont compiledFont))
            {
                World world = fontEntity.GetWorld();

                //because we know its a Font, we know it was loaded from bytes before so it must have that list
                USpan<byte> bytes = fontEntity.GetArray<byte>();
                Face face = freeType.Load(bytes.Address, bytes.Length);
                face.SetPixelSize(pixelSize, pixelSize);

                //generate a new texture atlas to be reused
                using List<AtlasTexture.InputSprite> inputSprites = new();
                USpan<char> name = stackalloc char[1];
                Array<IsGlyph> glyphs = new(glyphCount);
                for (uint i = 0; i < glyphCount; i++)
                {
                    rint glyphReference = fontEntity.GetArrayElementRef<FontGlyph>(i).value;
                    uint glyphEntity = fontEntity.GetReference(glyphReference);
                    Glyph glyph = new(world, glyphEntity);
                    char character = glyph.Character;
                    name[0] = character;

                    GlyphSlot slot = face.LoadGlyph(face.GetCharIndex(character));
                    Bitmap bitmap = slot.Render();
                    (uint x, uint y) size = bitmap.Size;
                    inputSprites.Add(new(name, size.x, size.y, Channels.Red, bitmap.Buffer));

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
                Debug.WriteLine($"Generated text atlas sized {atlas.Size} for font entity `{fontEntity}`");
            }

            return compiledFont;
        }
    }
}