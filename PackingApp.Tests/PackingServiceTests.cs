using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PackingApp.Shared;
using FluentAssertions;
using Xunit;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PackingApp.Tests
{
    public class PackingServiceTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly HttpClient _client;

        public PackingServiceTests(WebApplicationFactory<Program> factory)
        {
            _client = factory.CreateClient();
        }

        [Fact]
        public async Task Solve_ValidShapes_ReturnsPackedIndividual()
        {
            var shapes = new List<Rect>
            {
                new Rect(1, 1, 1),
                new Rect(2, 2, 2)
            };

            var response = await _client.PostAsJsonAsync("/api/packing/solve/test", shapes);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            
            var result = await response.Content.ReadFromJsonAsync<Individual>();
            result.Should().NotBeNull();
            result!.Rectangles.Should().HaveCount(2);
            result.Fitness.Should().BeGreaterThan(0); 
        }
    }
}
