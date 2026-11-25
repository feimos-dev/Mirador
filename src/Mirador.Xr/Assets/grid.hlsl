#include <stereokit.hlsli>
#include "home.hlsli"

float4 line_color  = { 0.034, 0.381, 0.939, 1 };
float4 floor_color = { 0.0025, 0.0035, 0.011, 1 };
float4 pool_color  = { 0.0045, 0.013, 0.042, 1 };
float4 mark_color  = { 0.456, 0.12, 0.687, 1 };
float4 fade        = { 3, 32, 0, 0 };
float4 pool        = { 3.5, 0.22, 0.38, 0 };
float4 mark        = { 0.45, 0.07, 0.01, 0.4 };

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

float grid_line(float2 at, float2 pixel) {
	float2 width  = max(pixel, 1e-6);
	float2 to     = abs(frac(at - 0.5) - 0.5) / width;
	float  cover  = 1 - saturate(min(to.x, to.y) - 0.25);
	float  crowd  = max(width.x, width.y);
	return cover * saturate(2 - crowd * 4);
}

float hexagon(float2 p, float apothem) {
	const float3 k = float3(-0.866025404, 0.5, 0.577350269);
	p  = abs(p);
	p -= 2 * min(dot(k.xy, p), 0) * k.xy;
	p -= float2(clamp(p.x, -k.z * apothem, k.z * apothem), apothem);
	return length(p) * sign(p.y);
}

float segment(float2 p, float2 a, float2 b) {
	float2 pa = p - a;
	float2 ba = b - a;
	return length(pa - ba * saturate(dot(pa, ba) / dot(ba, ba)));
}

float stroke(float to, float pixel) {
	float half_width = max(mark.z, pixel * 0.75);
	return 1 - saturate((to - half_width) / pixel + 0.5);
}

float centre_mark(float2 at, float2 pixel) {
	float size    = max(max(pixel.x, pixel.y), 1e-6);
	float apothem = mark.x * 0.866025404;
	float ring    = abs(hexagon(at.yx, apothem - mark.y) - mark.y);

	float  tip   = mark.x + 0.2;
	float2 side  = float2(abs(at.x), at.y);
	float  arrow = segment(side, float2(0, -tip), float2(0.12, 0.12 - tip));

	return max(stroke(ring, size), stroke(arrow, size)) * saturate(2 - size * 40);
}

float4 ps(psIn input) : SV_TARGET {
	float2 at    = input.world.xz;
	float2 pixel = fwidth(at);

	float dist = length(input.world - input.camera);
	float far  = smoothstep(0, 1, saturate((dist - fade.x) / (fade.y - fade.x)));
	float near = exp(-dot(at, at) / (pool.x * pool.x));

	float3 color = floor_color.rgb + pool_color.rgb * near;
	color = lerp(color, line_color.rgb, grid_line(at, pixel) * (pool.y + pool.z * near) * (1 - far));
	color = lerp(color, mark_color.rgb, centre_mark(at, pixel) * mark.w);
	color = lerp(color, sky_color(0), far);
	return float4(dither(color, input.pos.xy), 1);
}
