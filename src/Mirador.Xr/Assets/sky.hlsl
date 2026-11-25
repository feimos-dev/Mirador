#include <stereokit.hlsli>
#include "home.hlsli"

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
	return float4(dither(sky_color(normalize(input.dir).y), input.pos.xy), 1);
}
