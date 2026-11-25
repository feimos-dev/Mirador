// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include <catch2/catch_test_macros.hpp>

#include <string>
#include <vector>

#include "core/edid.hpp"
#include "core/product_table.hpp"
#include "fakes/edids.hpp"
#include "fakes/fake_api.hpp"

using nvdm::edids::bytes;

TEST_CASE("edid: the Dream Air's preferred DisplayID timing is its mode")
{
    const auto mode = nvdm::preferred_mode(bytes(nvdm::edids::kDreamAir));
    REQUIRE(mode.has_value());
    CHECK(mode->width == 7104);
    CHECK(mode->height == 3840);
    CHECK(mode->refresh_mhz == 90000);
}

TEST_CASE("edid: without a preferred DisplayID timing the base block's first timing is the mode")
{
    const auto mode = nvdm::preferred_mode(bytes(nvdm::edids::kPimax5kSuper));
    REQUIRE(mode.has_value());
    CHECK(mode->width == 2880);
    CHECK(mode->height == 2560);
    CHECK((mode->refresh_mhz + 500) / 1000 == 64);
}

TEST_CASE("edid: without either the first DisplayID timing is the mode")
{
    const auto mode = nvdm::preferred_mode(bytes(nvdm::edids::kPimax5kPlus));
    REQUIRE(mode.has_value());
    CHECK(mode->width == 2880);
    CHECK(mode->height == 2038);
    CHECK((mode->refresh_mhz + 500) / 1000 == 65);
}

TEST_CASE("edid: a base block without timings and no extension gives no mode")
{
    CHECK_FALSE(nvdm::preferred_mode(nvdm::FakeApi::make_edid(0x42d2, 0x1020)).has_value());
    CHECK_FALSE(nvdm::preferred_mode({}).has_value());
    CHECK_FALSE(nvdm::preferred_mode(std::vector<uint8_t>(127, 0)).has_value());
}

TEST_CASE("edid: a cut-off or lying DisplayID block is not read past its end")
{
    const std::vector<uint8_t> good = bytes(nvdm::edids::kDreamAir);

    std::vector<uint8_t> edid = good;
    edid[256 + 2] = 0xff;
    const auto section = nvdm::preferred_mode(edid);
    REQUIRE(section.has_value());
    CHECK(section->width == 7104);

    edid = good;
    edid[256 + 5 + 2] = 0xff;
    CHECK_FALSE(nvdm::preferred_mode(edid).has_value());

    edid = good;
    edid.resize(256 + 40);
    CHECK_FALSE(nvdm::preferred_mode(edid).has_value());
}

TEST_CASE("edid: a timing no display drives is no mode")
{
    std::vector<uint8_t> edid = bytes(nvdm::edids::kPimax5kSuper);
    edid.resize(128);
    REQUIRE(nvdm::preferred_mode(edid).has_value());

    edid[54] = 0xff;
    edid[55] = 0xff;
    edid[56] = 1;
    edid[57] = 1;
    edid[58] = 0;
    edid[59] = 1;
    edid[60] = 1;
    edid[61] = 0;
    CHECK_FALSE(nvdm::preferred_mode(edid).has_value());

    edid[54] = 1;
    edid[55] = 0;
    edid[56] = 0xff;
    edid[57] = 0xff;
    edid[58] = 0xff;
    edid[59] = 0xff;
    edid[60] = 0xff;
    edid[61] = 0xff;
    CHECK_FALSE(nvdm::preferred_mode(edid).has_value());
}

TEST_CASE("edid: Microsoft's HMD block is found in the CTA extension")
{
    CHECK(nvdm::has_microsoft_hmd_block(bytes(nvdm::edids::kDreamAirRegistry)));
    CHECK_FALSE(nvdm::has_microsoft_hmd_block(bytes(nvdm::edids::kDreamAir)));
    CHECK_FALSE(nvdm::has_microsoft_hmd_block(bytes(nvdm::edids::kPimax5kSuper)));
    CHECK_FALSE(nvdm::has_microsoft_hmd_block({}));
}

TEST_CASE("product: the Dream Air is the tested table's, with or without Microsoft's block")
{
    for (const std::string* hex : {&nvdm::edids::kDreamAir, &nvdm::edids::kDreamAirRegistry}) {
        const auto product = nvdm::find_product(bytes(*hex));
        REQUIRE(product.has_value());
        CHECK(product->tested);
        CHECK(std::string(product->name) == "Pimax Dream Air");
    }
}

TEST_CASE("product: another headset of a direct-mode vendor runs the mode of its EDID, untested")
{
    const auto product = nvdm::find_product(bytes(nvdm::edids::kPimax5kSuper));
    REQUIRE(product.has_value());
    CHECK_FALSE(product->tested);
    CHECK(product->pnp_product == 0x101b);
    CHECK(product->width == 2880);
    CHECK(product->height == 2560);
    CHECK(std::string(product->name) == "Pimax headset (PVR 0x101b)");

    CHECK(nvdm::find_product(bytes(nvdm::edids::kPimax5kSuper))->name == product->name);
}

TEST_CASE("product: the Dream Air's ids with another mode are another headset, untested")
{
    const auto product = nvdm::find_product(bytes(nvdm::edids::kPimax8kPlus));
    REQUIRE(product.has_value());
    CHECK_FALSE(product->tested);
    CHECK(product->pnp_product == 0x1019);
    CHECK(product->width == 2880);
    CHECK(product->height == 2560);
    CHECK(std::string(product->name) == "Pimax headset (PVR 0x1019)");

    std::vector<uint8_t> base = bytes(nvdm::edids::kDreamAir);
    base.resize(128);
    const auto dream_air = nvdm::find_product(base);
    REQUIRE(dream_air.has_value());
    CHECK(dream_air->tested);
}

TEST_CASE("product: a vendor the layer doesn't know, or an EDID without a mode, is no product")
{
    CHECK_FALSE(nvdm::find_product(nvdm::FakeApi::make_edid(0x10ac, 0x1019)).has_value());
    CHECK_FALSE(nvdm::find_product(nvdm::FakeApi::make_edid(0x42d2, 0x1020)).has_value());
}

TEST_CASE("vendors: Pimax first, its NVAPI id and its EDID id")
{
    REQUIRE(std::size(nvdm::kDirectModeVendors) >= 1);
    CHECK(nvdm::kDirectModeVendors[0].nvapi_id == 0xd242);
    CHECK(nvdm::kDirectModeVendors[0].pnp_vendor == 0x42d2);
    CHECK(std::string(nvdm::kDirectModeVendors[0].letters) == "PVR");
    CHECK(nvdm::vendor_for_pnp(0x42d2) == &nvdm::kDirectModeVendors[0]);
    CHECK(nvdm::vendor_for_pnp(0x10ac) == nullptr);
}
