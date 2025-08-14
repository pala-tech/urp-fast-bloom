namespace URPFastBloom
{
	using System;
	using System.Collections.Generic;
	using UnityEngine;
	using UnityEngine.Rendering;
	using UnityEngine.Rendering.RenderGraphModule;
	using UnityEngine.Rendering.Universal;
	using static RenderTextureUtils;

	public class BloomRenderPass : ScriptableRenderPass, IDisposable
	{
		static readonly int s_cameraColorTextureId = Shader.PropertyToID("_CameraColorTexture");
		RenderTextureDescriptor m_BloomTargetDescriptor;
		Vector2Int m_BloomTargetResolution;
		BloomSettings m_Settings;
		Material m_RgMaterial;

		public void Dispose()
		{
			if (m_RgMaterial != null)
			{
				CoreUtils.Destroy(m_RgMaterial);
				m_RgMaterial = null;
			}
		}

		public void SetUp(BloomSettings settings)
		{
			m_Settings = settings;

			if (m_RgMaterial == null && m_Settings != null && m_Settings.shader != null)
			{
				m_RgMaterial = new Material(m_Settings.shader) { hideFlags = HideFlags.HideAndDontSave };
			}
		}

		public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
		{
			var cameraData = frameData.Get<UniversalCameraData>();
			if (cameraData.camera != null && cameraData.camera.cameraType != CameraType.Game)
				return;

			var resources = frameData.Get<UniversalResourceData>();
			var src = resources.activeColorTexture;

			// Ensure material and settings
			if (m_RgMaterial == null && m_Settings != null && m_Settings.shader != null)
			{
				m_RgMaterial = new Material(m_Settings.shader) { hideFlags = HideFlags.HideAndDontSave };
			}

			if (m_RgMaterial == null)
				return;

			// Compute target descriptors
			m_BloomTargetResolution = GetScreenResolution(m_Settings.resolution);
			m_BloomTargetDescriptor = new RenderTextureDescriptor(
				m_BloomTargetResolution.x,
				m_BloomTargetResolution.y,
				RenderTextureFormat.ARGBHalf,
				0,
				0
			);

			var camDesc = cameraData.cameraTargetDescriptor;
			camDesc.depthBufferBits = 0;
			camDesc.msaaSamples = 1;
			camDesc.mipCount = 1;

			// Allocate RG textures
			var tempColor = UniversalRenderer.CreateRenderGraphTexture(
				renderGraph,
				camDesc,
				"_Bloom_TempColor",
				false
			);
			var bloomTarget = UniversalRenderer.CreateRenderGraphTexture(
				renderGraph,
				m_BloomTargetDescriptor,
				"_Bloom_Target",
				false
			);

			// Build pyramid descriptors and textures
			var downTextures = new List<TextureHandle>(m_Settings.iterations);
			var sizes = new List<Vector2Int>(m_Settings.iterations);
			for (var i = 0; i < m_Settings.iterations; i++)
			{
				var w = m_BloomTargetResolution.x >> (i + 1);
				var h = m_BloomTargetResolution.y >> (i + 1);
				if (w < 2 || h < 2)
					break;
				var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGBHalf, 0, 0);
				var tex = UniversalRenderer.CreateRenderGraphTexture(
					renderGraph,
					desc,
					$"_Bloom_Pyramid_{i}",
					false
				);
				downTextures.Add(tex);
				sizes.Add(new Vector2Int(w, h));
			}

			// Copy src -> tempColor to avoid read/write hazards on active color
			using (
				var builder = renderGraph.AddRasterRenderPass<CopyPassData>(
					"Bloom CopyToTemp",
					out var passData
				)
			)
			{
				passData.src = src;
				passData.desc = camDesc;
				passData.material = m_RgMaterial;
				passData.pass = -1; // copy
				builder.UseTexture(src);
				builder.SetRenderAttachment(tempColor, 0);
				builder.AllowPassCulling(false);
				builder.AllowGlobalStateModification(true);
				builder.SetRenderFunc(
					(CopyPassData data, RasterGraphContext ctx) =>
					{
						// Copy using fullscreen blit with default scale/bias
						Blitter.BlitTexture(ctx.cmd, data.src, new Vector4(1, 1, 0, 0), 0.0f, false);
					}
				);
			}

			// Prefilter: tempColor -> downTextures[0]
			if (downTextures.Count > 0)
			{
				using (
					var builder = renderGraph.AddRasterRenderPass<PrefilterPassData>(
						"Bloom Prefilter",
						out var passData
					)
				)
				{
					passData.src = tempColor;
					passData.dst = downTextures[0];
					passData.texelSize = new Vector2(
						1f / m_BloomTargetResolution.x,
						1f / m_BloomTargetResolution.y
					);
					passData.threshold = m_Settings.threshold;
					passData.softKnee = m_Settings.softKnee;
					passData.material = m_RgMaterial;
					builder.UseTexture(tempColor);
					builder.SetRenderAttachment(downTextures[0], 0);
					builder.AllowPassCulling(false);
					builder.AllowGlobalStateModification(true);
					builder.SetRenderFunc(
						(PrefilterPassData data, RasterGraphContext ctx) =>
						{
							var knee = Mathf.Max(data.threshold * data.softKnee, 0.0001f);
							var curve = new Vector3(data.threshold - knee, knee * 2, 0.25f / knee);
							ctx.cmd.SetGlobalFloat(BloomIds.ThresholdId, data.threshold);
							ctx.cmd.SetGlobalVector(BloomIds.CurveId, curve);
							ctx.cmd.SetGlobalVector(BloomIds.TexelSizeId, data.texelSize);
							Blitter.BlitTexture(
								ctx.cmd,
								data.src,
								new Vector4(1, 1, 0, 0),
								data.material,
								BloomPasses.PREFILTER_PASS
							);
						}
					);
				}
			}

			// Downsample chain
			for (var i = 1; i < downTextures.Count; i++)
			{
				var srcDown = downTextures[i - 1];
				var dstDown = downTextures[i];
				var srcSize = sizes[i - 1];
				using (
					var builder = renderGraph.AddRasterRenderPass<DownsamplePassData>(
						$"Bloom Downsample {i}",
						out var passData
					)
				)
				{
					passData.src = srcDown;
					passData.texelSize = new Vector2(1f / srcSize.x, 1f / srcSize.y);
					passData.material = m_RgMaterial;
					builder.UseTexture(srcDown);
					builder.SetRenderAttachment(dstDown, 0);
					builder.AllowPassCulling(false);
					builder.AllowGlobalStateModification(true);
					builder.SetRenderFunc(
						(DownsamplePassData data, RasterGraphContext ctx) =>
						{
							ctx.cmd.SetGlobalVector(BloomIds.TexelSizeId, data.texelSize);
							Blitter.BlitTexture(
								ctx.cmd,
								data.src,
								new Vector4(1, 1, 0, 0),
								data.material,
								BloomPasses.DOWNSAMPLE_PASS
							);
						}
					);
				}
			}

			// Upsample chain
			for (var i = downTextures.Count - 1; i - 1 >= 0; i--)
			{
				var srcUp = downTextures[i];
				var dstUp = downTextures[i - 1];
				var srcSize = sizes[i];
				using (
					var builder = renderGraph.AddRasterRenderPass<UpsamplePassData>(
						$"Bloom Upsample {i}",
						out var passData
					)
				)
				{
					passData.src = srcUp;
					passData.texelSize = new Vector2(1f / srcSize.x, 1f / srcSize.y);
					passData.material = m_RgMaterial;
					builder.UseTexture(srcUp);
					builder.SetRenderAttachment(dstUp, 0);
					builder.AllowPassCulling(false);
					builder.AllowGlobalStateModification(true);
					builder.SetRenderFunc(
						(UpsamplePassData data, RasterGraphContext ctx) =>
						{
							ctx.cmd.SetGlobalVector(BloomIds.TexelSizeId, data.texelSize);
							Blitter.BlitTexture(
								ctx.cmd,
								data.src,
								new Vector4(1, 1, 0, 0),
								data.material,
								BloomPasses.UPSAMPLE_PASS
							);
						}
					);
				}
			}

			// Final: use the upsampled result at the largest level (index 0) -> bloomTarget
			if (downTextures.Count > 0)
			{
				var last = downTextures[0];
				var lastSize = sizes[0];
				using (
					var builder = renderGraph.AddRasterRenderPass<FinalPassData>(
						"Bloom Final",
						out var passData
					)
				)
				{
					passData.src = last;
					passData.texelSize = new Vector2(1f / lastSize.x, 1f / lastSize.y);
					passData.intensity = m_Settings.intensity;
					passData.material = m_RgMaterial;
					builder.UseTexture(last);
					builder.SetRenderAttachment(bloomTarget, 0);
					builder.AllowPassCulling(false);
					builder.AllowGlobalStateModification(true);
					builder.SetRenderFunc(
						(FinalPassData data, RasterGraphContext ctx) =>
						{
							ctx.cmd.SetGlobalFloat(BloomIds.IntensityId, data.intensity);
							ctx.cmd.SetGlobalVector(BloomIds.TexelSizeId, data.texelSize);
							Blitter.BlitTexture(
								ctx.cmd,
								data.src,
								new Vector4(1, 1, 0, 0),
								data.material,
								BloomPasses.FINAL_PASS
							);
						}
					);
				}
			}

			// Combine: tempColor + bloomTarget -> finalColor
			var finalColor = UniversalRenderer.CreateRenderGraphTexture(
				renderGraph,
				camDesc,
				"_Bloom_FinalColor",
				false
			);
			using (
				var builder = renderGraph.AddRasterRenderPass<CombinePassData>(
					"Bloom Combine",
					out var passDataCombine
				)
			)
			{
				passDataCombine.source = tempColor;
				passDataCombine.bloom = bloomTarget;
				passDataCombine.material = m_RgMaterial;
				passDataCombine.noise = m_Settings.noise;
				builder.UseTexture(tempColor);
				builder.UseTexture(bloomTarget);
				builder.SetRenderAttachment(finalColor, 0);
				builder.AllowPassCulling(false);
				builder.AllowGlobalStateModification(true);
				builder.SetRenderFunc(
					(CombinePassData data, RasterGraphContext ctx) =>
					{
						// Bind via globals where needed; RG requires TextureHandle for bound textures
						// Noise is a Texture2D so we bind through the material
						data.material.SetTexture(BloomIds.NoiseTexId, data.noise);
						data.material.SetVector(BloomIds.NoiseTexScaleId, GetTextureScreenScale(data.noise));
						ctx.cmd.SetGlobalTexture(BloomIds.SourceTexId, data.source);
						Blitter.BlitTexture(
							ctx.cmd,
							data.bloom,
							new Vector4(1, 1, 0, 0),
							data.material,
							BloomPasses.COMBINE_PASS
						);
					}
				);
			}

			// Copy finalColor back to active color
			using (
				var builder = renderGraph.AddRasterRenderPass<CopyPassData>(
					"Bloom CopyBack",
					out var passData2
				)
			)
			{
				passData2.src = finalColor;
				passData2.desc = camDesc;
				builder.UseTexture(finalColor);
				builder.SetRenderAttachment(src, 0);
				builder.AllowPassCulling(false);
				builder.AllowGlobalStateModification(true);
				builder.SetRenderFunc(
					(CopyPassData data, RasterGraphContext ctx) =>
					{
						Blitter.BlitTexture(ctx.cmd, data.src, new Vector4(1, 1, 0, 0), 0.0f, false);
					}
				);
			}
		}

		static class BloomPasses
		{
			public const int PREFILTER_PASS = 0;
			public const int DOWNSAMPLE_PASS = 1;
			public const int UPSAMPLE_PASS = 2;
			public const int FINAL_PASS = 3;
			public const int COMBINE_PASS = 4;
		}

		static class BloomIds
		{
			public static readonly int CurveId = Shader.PropertyToID("_Curve");
			public static readonly int IntensityId = Shader.PropertyToID("_Intensity");
			public static readonly int NoiseTexId = Shader.PropertyToID("_NoiseTex");
			public static readonly int NoiseTexScaleId = Shader.PropertyToID("_NoiseTexScale");
			public static readonly int SourceTexId = Shader.PropertyToID("_SourceTex");
			public static readonly int TexelSizeId = Shader.PropertyToID("_TexelSize");
			public static readonly int ThresholdId = Shader.PropertyToID("_Threshold");
		}

		class CopyPassData
		{
			public TextureHandle src;
			public RenderTextureDescriptor desc;
			public Material material;
			public int pass;
		}

		class PrefilterPassData
		{
			public TextureHandle src;
			public TextureHandle dst;
			public Vector2 texelSize;
			public float threshold;
			public float softKnee;
			public Material material;
		}

		class DownsamplePassData
		{
			public TextureHandle src;
			public Vector2 texelSize;
			public Material material;
		}

		class UpsamplePassData
		{
			public TextureHandle src;
			public Vector2 texelSize;
			public Material material;
		}

		class FinalPassData
		{
			public TextureHandle src;
			public Vector2 texelSize;
			public float intensity;
			public Material material;
		}

		class CombinePassData
		{
			public TextureHandle source;
			public TextureHandle bloom;
			public Texture2D noise;
			public Material material;
		}
	}
}
