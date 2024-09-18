using Fonts;
using Fonts.Components;
using FreeType;
using Meshes;
using Meshes.Components;
using Rendering.Components;
using Rendering.Events;
using Simulation;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Textures;
using Unmanaged;
using Unmanaged.Collections;

namespace Rendering.Systems
{
    public class TextRenderingSystem : SystemBase
    {
        private readonly Library freeType;
        private readonly ComponentQuery<IsTextMeshRequest> textQuery;
        private readonly ComponentQuery<IsTextRenderer> textRendererQuery;
        private readonly UnmanagedDictionary<uint, uint> textRequestVersions;
        private readonly UnmanagedDictionary<uint, CompiledFont> compiledFonts;
        private readonly ConcurrentQueue<Operation> operations;

        public TextRenderingSystem(World world) : base(world)
        {
            freeType = new();
            textQuery = new();
            textRendererQuery = new();
            textRequestVersions = new();
            compiledFonts = new();
            operations = new();
            Subscribe<RenderUpdate>(Update);
        }

        public override void Dispose()
        {
            while (operations.TryDequeue(out Operation operation))
            {
                operation.Dispose();
            }

            foreach (uint fontEntity in compiledFonts.Keys)
            {
                compiledFonts[fontEntity].Dispose();
            }

            compiledFonts.Dispose();

            textRequestVersions.Dispose();
            textRendererQuery.Dispose();
            textQuery.Dispose();
            freeType.Dispose();
            base.Dispose();
        }

        private void Update(RenderUpdate update)
        {
            UpdateTextMeshes();
            UpdateTextRendererMaterials();
            PerformOperations();
        }

        private void UpdateTextMeshes()
        {
            textQuery.Update(world);
            foreach (var x in textQuery)
            {
                IsTextMeshRequest request = x.Component1;
                bool sourceChanged = false;
                uint textMeshEntity = x.entity;
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

        private void UpdateTextRendererMaterials()
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
                    CompiledFont compiledFont = compiledFonts[fontEntity];
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
                    operations.Enqueue(operation);
                }
            }
        }

        private void PerformOperations()
        {
            while (operations.TryDequeue(out Operation operation))
            {
                world.Perform(operation);
                operation.Dispose();
            }
        }

        private bool TryUpdateTextMesh((uint entity, IsTextMeshRequest request) input)
        {
            uint textMeshEntity = input.entity;
            rint fontReference = input.request.fontReference;
            Vector2 alignment = input.request.alignment;
            uint fontEntity = world.GetReference(textMeshEntity, fontReference);
            Font font = new(world, fontEntity);
            if (font.IsCompliant())
            {
                Operation operation = new();
                operation.SelectEntity(textMeshEntity);

                //reset the mesh
                if (!world.ContainsArray<MeshVertexPosition>(textMeshEntity))
                {
                    operation.CreateArray<MeshVertexPosition>(0);
                }

                if (!world.ContainsArray<uint>(textMeshEntity))
                {
                    operation.CreateArray<uint>(0);
                }

                if (!world.ContainsArray<MeshVertexUV>(textMeshEntity))
                {
                    operation.CreateArray<MeshVertexUV>(0);
                }

                if (world.ContainsArray<MeshVertexColor>(textMeshEntity))
                {
                    operation.DestroyArray<MeshVertexColor>();
                }

                USpan<char> text = world.GetArray<char>(textMeshEntity);
                GenerateTextMesh(ref operation, font, text, alignment);

                //update proof components to fulfil the type argument
                if (world.TryGetComponent(textMeshEntity, out IsTextMesh textMeshProof))
                {
                    textMeshProof.version++;
                    operation.SetComponent(textMeshProof);
                }
                else
                {
                    operation.AddComponent(new IsTextMesh());
                }

                if (world.TryGetComponent(textMeshEntity, out IsMesh meshProof))
                {
                    meshProof.version++;
                    operation.SetComponent(meshProof);
                }
                else
                {
                    operation.AddComponent(new IsMesh());
                }

                operations.Enqueue(operation);
                return true;
            }
            else
            {
                return false;
            }
        }

        private void GenerateTextMesh(ref Operation operation, Font font, USpan<char> text, Vector2 alignment)
        {
            Entity fontEntity = font.entity;
            uint glyphCount = fontEntity.GetArrayLength<FontGlyph>();
            int pixelSize = 32;

            //todo: fault: what if the font changes? this system has no way of knowing when to update the atlases+meshes involved
            CompiledFont compiledFont = GetOrCompileFont(fontEntity, glyphCount, pixelSize);

            uint lineHeight = font.LineHeight;
            int penX = 0;
            int penY = 0;
            (int xMin, int xMax, int yMin, int yMax) bounds = compiledFont.face.Bounds; //todo: put bounds info on font entities
            float descender = compiledFont.face.Descender;
            float verticalSpan = bounds.yMax - bounds.yMin;
            using UnmanagedArray<MeshVertexPosition> positions = new(text.Length * 4);
            using UnmanagedArray<MeshVertexUV> uvs = new(text.Length * 4);
            using UnmanagedArray<uint> indices = new(text.Length * 6);
            Vector2 maxPosition = default;
            for (uint i = 0; i < text.Length; i++)
            {
                char c = text[i];
                IsGlyph glyph = compiledFont.glyphs[c];
                if (c == '\n')
                {
                    penX = 0;
                    penY += (int)lineHeight;
                    continue;
                }

                (int x, int y) glyphOffset = glyph.offset;
                (int x, int y) glyphAdvance = glyph.advance;
                (int x, int y) glyphSize = glyph.size;
                (int x, int y) glyphBearing = glyph.bearing;
                float xOffset = (glyphAdvance.x - glyphSize.x) / 2;
                Vector4 region = compiledFont.regions[c];
                float glyphWidth = glyphSize.x / pixelSize;
                float glyphHeight = glyphSize.y / pixelSize;
                Vector3 origin = new(penX + (glyphOffset.x / pixelSize), penY + (glyphOffset.y / pixelSize), 0);
                origin.Y -= (glyphSize.y - glyphBearing.y) / pixelSize;
                Vector3 size = new(glyphWidth, glyphHeight, 0);
                origin /= 64f;
                size /= 64f;
                MeshVertexPosition first = origin;
                MeshVertexPosition second = origin + new Vector3(size.X, 0, 0);
                MeshVertexPosition third = origin + new Vector3(size.X, size.Y, 0);
                MeshVertexPosition fourth = origin + new Vector3(0, size.Y, 0);
                penX += glyphAdvance.x / pixelSize;
                //penY += advance.y / pixelSize;

                positions[(i * 4) + 0] = first;
                positions[(i * 4) + 1] = second;
                positions[(i * 4) + 2] = third;
                positions[(i * 4) + 3] = fourth;

                maxPosition = Vector2.Max(maxPosition, new(first.value.X, first.value.Y));
                maxPosition = Vector2.Max(maxPosition, new(second.value.X, second.value.Y));
                maxPosition = Vector2.Max(maxPosition, new(third.value.X, third.value.Y));
                maxPosition = Vector2.Max(maxPosition, new(fourth.value.X, fourth.value.Y));

                MeshVertexUV firstUv = new(region.X, region.W);
                MeshVertexUV secondUv = new(region.Z, region.W);
                MeshVertexUV thirdUv = new(region.Z, region.Y);
                MeshVertexUV fourthUv = new(region.X, region.Y);

                uvs[(i * 4) + 0] = firstUv;
                uvs[(i * 4) + 1] = secondUv;
                uvs[(i * 4) + 2] = thirdUv;
                uvs[(i * 4) + 3] = fourthUv;
            }

            uint vertexIndex = 0;
            for (uint i = 0; i < text.Length; i++)
            {
                indices[(i * 6) + 0] = vertexIndex;
                indices[(i * 6) + 1] = vertexIndex + 1;
                indices[(i * 6) + 2] = vertexIndex + 2;
                indices[(i * 6) + 3] = vertexIndex + 2;
                indices[(i * 6) + 4] = vertexIndex + 3;
                indices[(i * 6) + 5] = vertexIndex;
                vertexIndex += 4;
            }

            //align
            Vector2 alignOffset = new(maxPosition.X * alignment.X, maxPosition.Y * alignment.Y);
            for (uint i = 0; i < text.Length; i++)
            {
                ref MeshVertexPosition first = ref positions[(i * 4) + 0];
                ref MeshVertexPosition second = ref positions[(i * 4) + 1];
                ref MeshVertexPosition third = ref positions[(i * 4) + 2];
                ref MeshVertexPosition fourth = ref positions[(i * 4) + 3];
                first = new(first.value.X - alignOffset.X, first.value.Y - alignOffset.Y, first.value.Z);
                second = new(second.value.X - alignOffset.X, second.value.Y - alignOffset.Y, second.value.Z);
                third = new(third.value.X - alignOffset.X, third.value.Y - alignOffset.Y, third.value.Z);
                fourth = new(fourth.value.X - alignOffset.X, fourth.value.Y - alignOffset.Y, fourth.value.Z);
            }

            operation.ResizeArray<MeshVertexPosition>(positions.Length);
            operation.SetArrayElement(0, positions.AsSpan());
            operation.ResizeArray<MeshVertexUV>(uvs.Length);
            operation.SetArrayElement(0, uvs.AsSpan());
            operation.ResizeArray<uint>(indices.Length);
            operation.SetArrayElement(0, indices.AsSpan());
        }

        private unsafe CompiledFont GetOrCompileFont(Entity fontEntity, uint glyphCount, int pixelSize)
        {
            if (!compiledFonts.TryGetValue(fontEntity.value, out CompiledFont compiledFont))
            {
                //because we know its a Font, we know it was loaded from bytes before so it must have that list
                USpan<byte> bytes = fontEntity.GetArray<byte>();
                Face face = freeType.Load(bytes.pointer, bytes.Length);
                face.SetPixelSize((uint)pixelSize, (uint)pixelSize);

                //generate a new texture atlas to be reused
                using UnmanagedList<AtlasTexture.InputSprite> inputSprites = new();
                USpan<char> name = stackalloc char[1];
                UnmanagedArray<IsGlyph> glyphs = new(glyphCount);
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
                UnmanagedArray<Vector4> regions = new(glyphCount);
                for (uint i = 0; i < glyphCount; i++)
                {
                    regions[i] = atlas[i].region;
                }

                compiledFont = new(face, atlas, glyphs, regions);
                compiledFonts.Add(fontEntity.value, compiledFont);
                Console.WriteLine($"Generated text atlas sized {atlas.Size} for font entity `{fontEntity}`");
            }

            return compiledFont;
        }
    }
}