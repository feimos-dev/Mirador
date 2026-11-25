#include <stereokit.hlsli>

float4 horizon_color = { 0.065, 0.075, 0.095, 1 };
float4 zenith_color  = { 0.008, 0.009, 0.013, 1 };

struct vsIn {
	float4 pos : SV_Position;
};
struct psIn {
	float4 pos : SV_POSITION;
	float3 dir : TEXCOORD0;
};

psIn vs(vsIn input, sk_ids_t ids) {
	psIn o;
	float4 world = mul(float4(input.pos.xyz, 1), sk_inst[ids.inst].world);
	o.dir = world.xyz - sk_camera_pos[ids.view].xyz;
	o.pos = mul(world, sk_viewproj[ids.view]);
	return o;
}

float4 ps(psIn input) : SV_TARGET {
	float up = saturate(normalize(input.dir).y);
	return float4(lerp(horizon_color.rgb, zenith_color.rgb, pow(up, 0.55)), 1);
}
