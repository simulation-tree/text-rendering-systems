using Data.Events;
using Data.Systems;
using Fonts;
using Fonts.Events;
using Fonts.Systems;
using Meshes;
using Rendering.Events;
using Rendering.Systems;
using Simulation;
using System.Threading;
using System.Threading.Tasks;
using Unmanaged;

namespace Rendering.Tests
{
    public class TextMeshTests
    {
        [TearDown]
        public void CleanUp()
        {
            Allocations.ThrowIfAny();
        }

        private async Task Simulate(World world, CancellationToken cancellation)
        {
            world.Submit(new DataUpdate());
            world.Submit(new FontUpdate());
            world.Submit(new RenderUpdate());
            world.Poll();
            await Task.Delay(1, cancellation);
        }

        [Test, CancelAfter(4000)]
        public async Task GenerateTextMesh(CancellationToken cancellation)
        {
            string sampleText = "What is up";
            using World world = new();
            using DataImportSystem dataImports = new(world);
            using FontImportSystem fonts = new(world);
            using TextRasterizationSystem textRendering = new(world);

            Font arialFont = new(world, "*/Arial.otf");
            TextMesh textMesh = new(world, sampleText, arialFont);
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
