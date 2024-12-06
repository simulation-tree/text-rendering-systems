using Data;
using Data.Components;
using Data.Systems;
using Fonts;
using Fonts.Components;
using Fonts.Systems;
using Meshes;
using Meshes.Components;
using Rendering;
using Rendering.Components;
using Simulation.Components;
using Simulation.Tests;
using System.Threading;
using System.Threading.Tasks;
using Textures;
using Textures.Components;
using Worlds;

namespace TextRendering.Systems.Tests
{
    public class TextMeshTests : SimulationTests
    {
        protected override void SetUp()
        {
            base.SetUp();
            ComponentType.Register<IsMesh>();
            ComponentType.Register<IsTextMesh>();
            ComponentType.Register<IsTextMeshRequest>();
            ComponentType.Register<IsTextRenderer>();
            ComponentType.Register<IsFont>();
            ComponentType.Register<IsFontRequest>();
            ComponentType.Register<IsGlyph>();
            ComponentType.Register<IsTexture>();
            ComponentType.Register<IsTextureRequest>();
            ComponentType.Register<IsDataRequest>();
            ComponentType.Register<IsDataSource>();
            ComponentType.Register<IsData>();
            ComponentType.Register<IsProgram>();
            ComponentType.Register<FontMetrics>();
            ComponentType.Register<FontName>();
            ArrayType.Register<BinaryData>();
            ArrayType.Register<AtlasSprite>();
            ArrayType.Register<Pixel>();
            ArrayType.Register<Kerning>();
            ArrayType.Register<FontGlyph>();
            ArrayType.Register<TextCharacter>();
            ArrayType.Register<MeshVertexPosition>();
            ArrayType.Register<MeshVertexNormal>();
            ArrayType.Register<MeshVertexUV>();
            ArrayType.Register<MeshVertexColor>();
            ArrayType.Register<MeshVertexTangent>();
            ArrayType.Register<MeshVertexBiTangent>();
            ArrayType.Register<MeshVertexIndex>();
            Simulator.AddSystem(new DataImportSystem());
            Simulator.AddSystem(new FontImportSystem());
            Simulator.AddSystem(new TextRasterizationSystem());
        }

        [Test, CancelAfter(4000)]
        public async Task GenerateTextMesh(CancellationToken cancellation)
        {
            EmbeddedAddress.Register(GetType().Assembly, "Assets/Arial.otf");

            string sampleText = "What is up";
            Font arialFont = new(World, "*/Arial.otf");
            TextMesh textMesh = new(World, sampleText, arialFont);
            await textMesh.UntilCompliant(Simulate, cancellation);

            Mesh mesh = textMesh;
            Assert.That(mesh.HasPositions, Is.True);
            Assert.That(mesh.HasNormals, Is.False);
            Assert.That(mesh.HasUVs, Is.True);
            Assert.That(mesh.HasColors, Is.False);
            Assert.That(mesh.Positions.Length, Is.EqualTo(sampleText.Length * 4));
            Assert.That(mesh.VertexCount, Is.EqualTo(sampleText.Length * 4));

            //todo: write assets to verify the generation with the arial font
        }
    }
}
