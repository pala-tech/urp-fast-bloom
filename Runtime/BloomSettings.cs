namespace URPFastBloom
{
	using System;
	using UnityEngine;

	[Serializable]
	public partial class BloomSettings
	{
		[HideInInspector]
		public Shader shader;

		[HideInInspector]
		public Texture2D noise;

		[Header("Settings"), Range(64, 1024)]
		public int resolution = 512;

		[Range(2, 8)]
		public int iterations = 8;

		[Range(0, 10)]
		public float intensity = 0.8f;

		[Range(0, 10)]
		public float threshold = 0.6f;

		[Range(0, 1)]
		public float softKnee = 0.7f;
	}
}
