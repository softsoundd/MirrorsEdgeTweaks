using MirrorsEdgeTweaks.Controls;
using MirrorsEdgeTweaks.Tests.TestSupport;
using System.Windows;
using System.Windows.Controls;

namespace MirrorsEdgeTweaks.Tests
{
    [Collection("Wpf")]
    public class SectionExpanderResourceTests
    {
        [Fact]
        public void SectionExpander_styles_resolve_and_apply_template()
        {
            StaWpfTestRunner.RunWithAppResources(app =>
            {
                var sectionStyle = app.FindResource("SectionExpander") as Style;
                var subSectionStyle = app.FindResource("SubSectionExpander") as Style;

                Assert.NotNull(sectionStyle);
                Assert.NotNull(subSectionStyle);

                var sectionExpander = new Expander
                {
                    Style = sectionStyle,
                    Header = "Test Section"
                };
                sectionExpander.ApplyTemplate();
                Assert.True(sectionExpander.IsExpanded);

                var subSectionExpander = new Expander
                {
                    Style = subSectionStyle,
                    Header = "Test Subsection"
                };
                subSectionExpander.ApplyTemplate();
                Assert.False(subSectionExpander.IsExpanded);

                Assert.NotNull(SectionExpanderProperties.GetHorizontalHeaderStyle(sectionExpander));
                Assert.NotNull(SectionExpanderProperties.GetHorizontalHeaderStyle(subSectionExpander));
            });
        }
    }

    [CollectionDefinition("Wpf", DisableParallelization = true)]
    public sealed class WpfTestCollection;
}
