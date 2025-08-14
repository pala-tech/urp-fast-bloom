#if UNITY_EDITOR
namespace URPFastBloom
{
	using UnityEngine;

	public partial class BloomSettings
	{
		public void AutoDetect()
		{
			if (!shader)
				shader = UnityEditor.AssetDatabase.LoadAssetByGUID<Shader>(
					new UnityEditor.GUID("ca96bfe3611524ee3be8cd2efb98348f")
				);
			if (!noise)
				noise = UnityEditor.AssetDatabase.LoadAssetByGUID<Texture2D>(
					new UnityEditor.GUID("50b54341495978843a6f85583ed4417d")
				);
		}
	}
}
#endif
