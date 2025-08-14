using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace PostEffects
{
	public class BloomRenderPass : ScriptableRenderPass, IDisposable
	{
		private static readonly int CameraColorTextureId = Shader.PropertyToID("_CameraColorTexture");
		private RenderTextureDescriptor _bloomTargetDescriptor;
		private Vector2Int _bloomTargetResolution;
		private BloomSettings _settings;
		private Material _rgMaterial;

		public void Dispose()
		{
			if (_rgMaterial != null)
			{
				CoreUtils.Destroy(_rgMaterial);
				_rgMaterial = null;
			}
		}

		public void SetUp(BloomSettings settings)
		{
			_settings = settings;

			if (_rgMaterial == null && _settings != null && _settings.Shader != null)
			{
				_rgMaterial = new Material(_settings.Shader) { hideFlags = HideFlags.HideAndDontSave };
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
			if (_rgMaterial == null && _settings != null && _settings.Shader != null)
			{
				_rgMaterial = new Material(_settings.Shader) { hideFlags = HideFlags.HideAndDontSave };
			}
			if (_rgMaterial == null)
				return;

			// Compute target descriptors
			_bloomTargetResolution = RenderTextureUtils.GetScreenResolution(_settings.Resolution);
			_bloomTargetDescriptor = new RenderTextureDescriptor(
				_bloomTargetResolution.x,
				_bloomTargetResolution.y,
				Ext.argbHalf,
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
				_bloomTargetDescriptor,
				"_Bloom_Target",
				false
			);

			// Build pyramid descriptors and textures
			var downTextures = new List<TextureHandle>(_settings.Iterations);
			var sizes = new List<Vector2Int>(_settings.Iterations);
			for (var i = 0; i < _settings.Iterations; i++)
			{
				var w = _bloomTargetResolution.x >> (i + 1);
				var h = _bloomTargetResolution.y >> (i + 1);
				if (w < 2 || h < 2)
					break;
				var desc = new RenderTextureDescriptor(w, h, Ext.argbHalf, 0, 0);
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
				passData.material = _rgMaterial;
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
						1f / _bloomTargetResolution.x,
						1f / _bloomTargetResolution.y
					);
					passData.threshold = _settings.Threshold;
					passData.softKnee = _settings.SoftKnee;
					passData.material = _rgMaterial;
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
								BloomPasses.PrefilterPass
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
					passData.material = _rgMaterial;
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
								BloomPasses.DownsamplePass
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
					passData.material = _rgMaterial;
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
								BloomPasses.UpsamplePass
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
					passData.intensity = _settings.Intensity;
					passData.material = _rgMaterial;
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
								BloomPasses.FinalPass
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
				passDataCombine.material = _rgMaterial;
				passDataCombine.noise = _settings.Noise;
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
						data.material.SetVector(
							BloomIds.NoiseTexScaleId,
							RenderTextureUtils.GetTextureScreenScale(data.noise)
						);
						ctx.cmd.SetGlobalTexture(BloomIds.SourceTexId, data.source);
						Blitter.BlitTexture(
							ctx.cmd,
							data.bloom,
							new Vector4(1, 1, 0, 0),
							data.material,
							BloomPasses.CombinePass
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

		private static class BloomPasses
		{
			public const int PrefilterPass = 0;
			public const int DownsamplePass = 1;
			public const int UpsamplePass = 2;
			public const int FinalPass = 3;
			public const int CombinePass = 4;
		}

		private static class BloomIds
		{
			public static readonly int CurveId = Shader.PropertyToID("_Curve");
			public static readonly int IntensityId = Shader.PropertyToID("_Intensity");
			public static readonly int NoiseTexId = Shader.PropertyToID("_NoiseTex");
			public static readonly int NoiseTexScaleId = Shader.PropertyToID("_NoiseTexScale");
			public static readonly int SourceTexId = Shader.PropertyToID("_SourceTex");
			public static readonly int TexelSizeId = Shader.PropertyToID("_TexelSize");
			public static readonly int ThresholdId = Shader.PropertyToID("_Threshold");
		}

		private class CopyPassData
		{
			public TextureHandle src;
			public RenderTextureDescriptor desc;
			public Material material;
			public int pass;
		}

		private class PrefilterPassData
		{
			public TextureHandle src;
			public TextureHandle dst;
			public Vector2 texelSize;
			public float threshold;
			public float softKnee;
			public Material material;
		}

		private class DownsamplePassData
		{
			public TextureHandle src;
			public Vector2 texelSize;
			public Material material;
		}

		private class UpsamplePassData
		{
			public TextureHandle src;
			public Vector2 texelSize;
			public Material material;
		}

		private class FinalPassData
		{
			public TextureHandle src;
			public Vector2 texelSize;
			public float intensity;
			public Material material;
		}

		private class CombinePassData
		{
			public TextureHandle source;
			public TextureHandle bloom;
			public Texture2D noise;
			public Material material;
		}
	}
}
