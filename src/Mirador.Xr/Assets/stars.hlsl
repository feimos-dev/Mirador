#include <stereokit.hlsli>

float4 core = { 0.3333, 1.2, 0, 0 };

struct vsIn {
	float4 pos  : SV_Position;
	float3 norm : NORMAL0;
	float2 uv   : TEXCOORD0;
	float4 col  : COLOR0;
};
struct psIn {
	float4 pos   : SV_POSITION;
	float2 uv    : TEXCOORD0;
	float3 light : COLOR0;
};

psIn vs(vsIn input, sk_ids_t ids) {
	psIn o;
	float4 world = mul(float4(input.pos.xyz, 1), sk_inst[ids.inst].world);
	o.pos   = mul(world, sk_viewproj[ids.view]);
	o.uv    = input.uv;
	o.light = input.col.rgb * input.col.a * input.col.a;
	return o;
}

float4 ps(psIn input) : SV_TARGET {
	float pixel  = length(fwidth(input.uv)) * 0.7071;
	float radius = max(core.x, pixel * core.y);
	float shape  = saturate(1 - length(input.uv) / radius);
	float spread = core.x / radius;
	return float4(input.light * shape * shape * spread * spread, 1);
}
