using ScionCarAssistant;

public class HelperFunctions
{
  public record Song(string Name, string Artist);
  public static Song[] RecommendSongs(MainPage.SongCountType[] songArray)
  {
    List<Song> topThree = [];

    if (songArray.Length < 3)
    {
      return [];
    }

    for (int i = 0; i < 3; i++)
    {
      topThree.Add(new Song(songArray[i].Name, songArray[i].Artist));
    }
    return topThree.ToArray(); ;
  }

}