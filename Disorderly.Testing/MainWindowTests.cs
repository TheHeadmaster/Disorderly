using System.Threading;
using NUnit.Framework;

namespace Disorderly.Testing;

[TestFixture]
[Apartment(ApartmentState.STA)]
public class MainWindowTests
{
    [Test]
    public void ConstructorLoadsWindow()
    {
        MainWindow window = new();

        try
        {
            Assert.That(window.Title, Is.EqualTo("Disorderly"));
            Assert.That(window.Content, Is.Not.Null);
        }
        finally
        {
            window.Close();
        }
    }
}