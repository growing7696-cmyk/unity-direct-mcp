using UnityEngine;
public class Spinner : MonoBehaviour {
  public float speed = 90f;
  void Update() { transform.Rotate(0, speed * Time.deltaTime, 0); if (Time.frameCount % 60 == 0) Debug.Log("spin " + transform.eulerAngles.y); }
}
