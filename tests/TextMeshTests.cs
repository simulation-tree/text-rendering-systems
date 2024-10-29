using Data.Systems;
using Fonts;
using Fonts.Systems;
using Meshes;
using Rendering.Systems;
using Simulation.Tests;
using System.Threading;
using System.Threading.Tasks;

namespace Rendering.Tests
{
    public class TextMeshTests : SimulationTests
    {
        protected override void SetUp()
        {
            base.SetUp();
            Simulator.AddSystem<DataImportSystem>();
            Simulator.AddSystem<FontImportSystem>();
            Simulator.AddSystem<TextRasterizationSystem>();
        }

        [Test, CancelAfter(4000)]
        public async Task GenerateTextMesh(CancellationToken cancellation)
        {
            string sampleText = "What is up";
            Font arialFont = new(World, "*/Arial.otf");
            TextMesh textMesh = new(World, sampleText, arialFont);
            await textMesh.UntilCompliant(Simulate, cancellation);

            Mesh mesh = textMesh.mesh;
            Assert.That(mesh.HasPositions, Is.True);
            Assert.That(mesh.HasNormals, Is.False);
            Assert.That(mesh.HasUVs, Is.True);
            Assert.That(mesh.HasColors, Is.False);
            Assert.That(mesh.Positions.Length, Is.EqualTo(sampleText.Length * 4));
            Assert.That(mesh.VertexCount, Is.EqualTo(sampleText.Length * 4));
        }
    }
}
