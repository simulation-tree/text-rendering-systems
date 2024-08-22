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
using System.Numerics;
using Textures;
using Unmanaged.Collections;

namespace Rendering.Systems
{
    public class TextRenderingSystem : SystemBase
    {
        private readonly Library freeType;
        private readonly Query<IsTextMeshRequest> textQuery;
        private readonly Query<IsTextRenderer> textRendererQuery;
        private readonly UnmanagedDictionary<eint, uint> textRequestVersions;
        private readonly UnmanagedDictionary<eint, CompiledFont> compiledFonts;
        private readonly ConcurrentQueue<Operation> operations;

        public TextRenderingSystem(World world) : base(world)
        {
            freeType = new();
            textQuery = new(world);
            textRendererQuery = new(world);
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

            foreach (eint fontEntity in compiledFonts.Keys)
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
            textQuery.Update();
            foreach (var x in textQuery)
            {
                IsTextMeshRequest request = x.Component1;
                bool sourceChanged = false;
                eint textMeshEntity = x.entity;
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
                        textRequestVersions[textMeshEntity] = request.version;
                    }
                }
            }
        }

        private void UpdateTextRendererMaterials()
        {
            textRendererQuery.Update();
            foreach (var x in textRendererQuery)
            {
                IsTextRenderer textRenderer = x.Component1;
                eint textRendererEntity = x.entity;
                rint meshReference = textRenderer.meshReference;
                eint meshEntity = world.GetReference(textRendererEntity, meshReference);
                if (!world.ContainsComponent<IsRenderer>(textRendererEntity))
                {
                    rint materialReference = textRenderer.materialReference;
                    rint fontReference = textRenderer.fontReference;
                    eint materialEntity = world.GetReference(textRendererEntity, materialReference);
                    eint fontEntity = world.GetReference(textRendererEntity, fontReference);
                    CompiledFont compiledFont = compiledFonts[fontEntity];
                    Material material = new(world, materialEntity);
                    Operation operation = new();
                    operation.SelectEntity(materialEntity);
                    if (material.TryGetTextureBinding(0, 0, out uint index))
                    {
                        MaterialTextureBinding binding = world.GetListElement<MaterialTextureBinding>(materialEntity, index);
                        binding.SetTexture(compiledFont.atlas);
                        operation.SetListElement(index, binding);
                    }
                    else
                    {
                        MaterialTextureBinding binding = new(0, new(0, 0), compiledFont.atlas, new(0, 0, 1, 1));
                        operation.AppendToList(binding);
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

        private bool TryUpdateTextMesh((eint entity, IsTextMeshRequest request) input)
        {
            eint textMeshEntity = input.entity;
            rint fontReference = input.request.fontReference;
            eint fontEntity = world.GetReference(textMeshEntity, fontReference);
            Font font = new(world, fontEntity);
            if (font.Is())
            {
                Operation operation = new();
                operation.SelectEntity(textMeshEntity);

                //reset the mesh
                if (world.ContainsList<MeshVertexPosition>(textMeshEntity))
                {
                    operation.ClearList<MeshVertexPosition>();
                }
                else
                {
                    operation.CreateList<MeshVertexPosition>();
                }

                if (world.ContainsList<uint>(textMeshEntity))
                {
                    operation.ClearList<uint>();
                }
                else
                {
                    operation.CreateList<uint>();
                }

                if (world.ContainsList<MeshVertexUV>(textMeshEntity))
                {
                    operation.ClearList<MeshVertexUV>();
                }
                else
                {
                    operation.CreateList<MeshVertexUV>();
                }

                if (world.ContainsList<MeshVertexColor>(textMeshEntity))
                {
                    operation.DestroyList<MeshVertexColor>();
                }

                UnmanagedList<char> text = world.GetList<char>(textMeshEntity);
                GenerateTextMesh(ref operation, font, text);

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

        private void GenerateTextMesh(ref Operation operation, Font font, UnmanagedList<char> text)
        {
            Entity fontEntity = font;
            uint glyphCount = world.GetListLength<FontGlyph>(fontEntity);
            int pixelSize = 32;

            //todo: fault: what if the font changes? this system has no way of knowing when to update the atlases+meshes involved
            if (!compiledFonts.TryGetValue(fontEntity, out CompiledFont compiledFont))
            {
                //because we know its a Font, we know it was loaded from bytes before so it must have that list
                UnmanagedList<byte> bytes = world.GetList<byte>(fontEntity);
                Face face = freeType.Load(bytes.AsSpan());
                face.SetPixelSize((uint)pixelSize, (uint)pixelSize);

                //generate a new texture atlas to be reused
                using UnmanagedList<AtlasTexture.InputSprite> inputSprites = new();
                Span<char> name = stackalloc char[1];
                UnmanagedArray<IsGlyph> glyphs = new(glyphCount);
                for (uint i = 0; i < glyphCount; i++)
                {
                    rint glyphReference = world.GetListElement<FontGlyph>(fontEntity, i).value;
                    eint glyphEntity = world.GetReference(fontEntity, glyphReference);
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
                compiledFonts.Add(fontEntity, compiledFont);
            }

            uint lineHeight = font.LineHeight;
            int penX = 0;
            int penY = 0;
            (int xMin, int xMax, int yMin, int yMax) bounds = compiledFont.face.Bounds; //todo: put bounds info on font entities
            using UnmanagedArray<MeshVertexPosition> positions = new(text.Count * 4);
            using UnmanagedArray<MeshVertexUV> uvs = new(text.Count * 4);
            using UnmanagedArray<uint> indices = new(text.Count * 6);
            for (uint i = 0; i < text.Count; i++)
            {
                char c = text[i];
                IsGlyph glyph = compiledFont.glyphs[c];
                if (c == '\n')
                {
                    penX = 0;
                    penY += (int)lineHeight;
                    continue;
                }

                (int x, int y) offset = glyph.offset;
                (int x, int y) advance = glyph.advance;
                (int x, int y) glyphSize = glyph.size;
                (int x, int y) bearing = glyph.bearing;
                int xOffset = (advance.x - glyphSize.x) / 2;
                int yOffset = bounds.yMax - bearing.y;
                Vector4 region = compiledFont.regions[c];
                float glyphWidth = glyphSize.x / pixelSize;
                float glyphHeight = glyphSize.y / pixelSize;
                Vector3 origin = new Vector3(penX, penY, 0) + new Vector3(offset.x, -offset.y, 0);
                Vector3 size = new(glyphWidth, glyphHeight, 0);
                origin /= 64f;
                size /= 64f;
                MeshVertexPosition first = origin;
                MeshVertexPosition second = origin + new Vector3(size.X, 0, 0);
                MeshVertexPosition third = origin + new Vector3(size.X, size.Y, 0);
                MeshVertexPosition fourth = origin + new Vector3(0, size.Y, 0);
                penX += advance.x / pixelSize;
                //penY += advance.y / pixelSize;

                positions[(i * 4) + 0] = first;
                positions[(i * 4) + 1] = second;
                positions[(i * 4) + 2] = third;
                positions[(i * 4) + 3] = fourth;

                MeshVertexUV firstUv = new(region.X, region.Y);
                MeshVertexUV secondUv = new(region.X + region.Z, region.Y);
                MeshVertexUV thirdUv = new(region.X + region.Z, region.Y + region.W);
                MeshVertexUV fourthUv = new(region.X, region.Y + region.W);

                uvs[(i * 4) + 0] = firstUv;
                uvs[(i * 4) + 1] = secondUv;
                uvs[(i * 4) + 2] = thirdUv;
                uvs[(i * 4) + 3] = fourthUv;
            }

            uint vertexIndex = 0;
            for (uint i = 0; i < text.Count; i++)
            {
                indices[(i * 6) + 0] = vertexIndex;
                indices[(i * 6) + 1] = vertexIndex + 1;
                indices[(i * 6) + 2] = vertexIndex + 2;
                indices[(i * 6) + 3] = vertexIndex + 2;
                indices[(i * 6) + 4] = vertexIndex + 3;
                indices[(i * 6) + 5] = vertexIndex;
                vertexIndex += 4;
            }

            operation.AppendToList<MeshVertexPosition>(positions.AsSpan());
            operation.AppendToList<MeshVertexUV>(uvs.AsSpan());
            operation.AppendToList<uint>(indices.AsSpan());
        }
    }
}