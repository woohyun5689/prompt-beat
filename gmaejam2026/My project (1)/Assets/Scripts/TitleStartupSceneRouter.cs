using UnityEngine;
using UnityEngine.SceneManagement;

public static class TitleStartupSceneRouter
{
    private const string TitleSceneName = "TitleScene";

    private static bool checkedStartScene;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void RouteFirstSceneToTitle()
    {
        if (checkedStartScene)
        {
            return;
        }

        checkedStartScene = true;

        if (SceneManager.GetActiveScene().name != TitleSceneName)
        {
            SceneManager.LoadScene(TitleSceneName);
        }
    }
}
