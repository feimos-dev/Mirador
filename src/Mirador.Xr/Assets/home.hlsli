static const float3 zenith_color = { 0.0015, 0.0025, 0.009 };
static const float3 haze_color   = { 0.008, 0.02, 0.075 };
static const float3 glow_color   = { 0.16, 0.07, 0.34 };
static const float  glow_fall    = 160;

float3 sky_color(float height) {
	return zenith_color + haze_color * exp(-saturate(height) * 3.5) + glow_color * exp(-abs(height) * glow_fall);
}

float3 dither(float3 color, float2 pixel) {
	float  noise = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
	float3 gamma = pow(max(color, 0), 1 / 2.2);
	return pow(max(gamma + (noise - 0.5) / 255, 0), 2.2);
}
