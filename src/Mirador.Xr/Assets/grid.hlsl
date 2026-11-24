#include <stereokit.hlsli>

//--name = mirador/grid

// The home's floor: a grid of 1 m lines, fading into the horizon together with the floor.
float4 line_color    = { 0.42, 0.46, 0.53, 1 };
float4 floor_color   = { 0.018, 0.02, 0.026, 1 };
float4 horizon_color = { 0.065, 0.075, 0.095, 1 };
// x: how far from the eye the floor starts to fade, y: where it has become the horizon
float4 fade          = { 3, 32, 0, 0 };

struct vsIn {
	float4 pos : SV_Position;
};
struct psIn {
	float4 pos    : SV_POSITION;
	float3 world  : TEXCOORD0;
	float3 camera : TEXCOORD1;
};

psIn vs(vsIn input, sk_ids_t ids) {
	psIn o;
	float4 world = mul(float4(input.pos.xyz, 1), sk_inst[ids.inst].world);
	o.world  = world.xyz;
	o.camera = sk_camera_pos[ids.view].xyz;
	o.pos    = mul(world, sk_viewproj[ids.view]);
	return o;
}

// How much of a pixel a line of the grid covers: lines are about a pixel and a half wide whatever their distance, and
// thin out only where a cell would be smaller than two pixels, where they would shimmer.
float grid_line(float2 at, float2 pixel) {
	float2 width  = max(pixel, 1e-6);
	float2 to     = abs(frac(at - 0.5) - 0.5) / width;
	float  cover  = 1 - saturate(min(to.x, to.y) - 0.25);
	float  crowd  = max(width.x, width.y);
	return cover * saturate(2 - crowd * 4);
}

float4 ps(psIn input) : SV_TARGET {
	float2 at    = input.world.xz;
	float2 pixel = fwidth(at);

	float lines = grid_line(at, pixel) * 0.5;

	float dist = length(input.world - input.camera);
	float far  = smoothstep(0, 1, saturate((dist - fade.x) / (fade.y - fade.x)));

	float3 color = lerp(floor_color.rgb, line_color.rgb, lines * (1 - far));
	color = lerp(color, horizon_color.rgb, far);
	return float4(color, 1);
}
