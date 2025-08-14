namespace URPFastBloom
{
	using UnityEngine;
	using UnityEngine.Rendering.Universal;

	public class BloomRendererFeature : ScriptableRendererFeature
	{
		[SerializeField]
		BloomSettings settings;

		BloomRenderPass m_Pass;

		public override void Create()
		{
			settings ??= new();
#if UNITY_EDITOR
			settings.AutoDetect();
#endif
			m_Pass = new BloomRenderPass
			{
				renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing,
			};
		}

		public override void AddRenderPasses(
			ScriptableRenderer renderer,
			ref RenderingData renderingData
		)
		{
			if (renderingData.cameraData.cameraType != CameraType.Game)
				return;

			m_Pass.SetUp(settings);
			renderer.EnqueuePass(m_Pass);
		}

		protected override void Dispose(bool disposing)
		{
			m_Pass?.Dispose();
		}
	}
}
