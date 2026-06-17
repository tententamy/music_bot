namespace VoSiBot;

class Note
{
    public float X, Y, Vy, Life;
    public string Symbol = "♪";
}

record TrackInfo(string Title, string AudioUrl, string SourceUrl);
