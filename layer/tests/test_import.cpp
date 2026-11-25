// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include <catch2/catch_test_macros.hpp>

#include <iterator>
#include <optional>
#include <string>
#include <vector>

#include "layer/import_rules.hpp"
#include "layer/surface.hpp"

namespace {

constexpr VkImageUsageFlags kStorage = VK_IMAGE_USAGE_STORAGE_BIT;
constexpr VkImageUsageFlags kColour = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
constexpr VkImageUsageFlags kSampledTransfer =
    VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
constexpr VkExtent2D kDreamAirExtent{7104, 3840};

using Formats = std::vector<VkFormat>;

VkPhysicalDeviceMemoryProperties memory_types(std::initializer_list<VkMemoryPropertyFlags> types)
{
    VkPhysicalDeviceMemoryProperties memory{};
    for (const VkMemoryPropertyFlags flags : types) {
        memory.memoryTypes[memory.memoryTypeCount].propertyFlags = flags;
        memory.memoryTypes[memory.memoryTypeCount].heapIndex = 0;
        memory.memoryTypeCount++;
    }
    memory.memoryHeapCount = 1;
    return memory;
}

constexpr VkMemoryPropertyFlags kHost = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;
constexpr VkMemoryPropertyFlags kLocal = VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT;

VkImageFormatProperties big_enough()
{
    VkImageFormatProperties p{};
    p.maxExtent = {16384, 16384, 1};
    p.maxMipLevels = 15;
    p.maxArrayLayers = 2048;
    p.sampleCounts = VK_SAMPLE_COUNT_1_BIT;
    return p;
}

VkExternalMemoryProperties external(VkExternalMemoryFeatureFlags features)
{
    VkExternalMemoryProperties e{};
    e.externalMemoryFeatures = features;
    e.compatibleHandleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_KMT_BIT;
    return e;
}

}

TEST_CASE("trial_configs_are_monados_two_compositors")
{
    CHECK(dvl::kComputeConfig.format == VK_FORMAT_R8G8B8A8_UNORM);
    CHECK(dvl::kComputeConfig.usage == (kStorage | kSampledTransfer));
    CHECK(dvl::kGraphicsConfig.format == VK_FORMAT_R8G8B8A8_SRGB);
    CHECK(dvl::kGraphicsConfig.usage == (kColour | kSampledTransfer));
    REQUIRE(std::size(dvl::kTrialConfigs) == 2);
    CHECK(dvl::kTrialConfigs[0].format == dvl::kComputeConfig.format);
    CHECK(dvl::kTrialConfigs[1].format == dvl::kGraphicsConfig.format);
    CHECK((dvl::kComputeConfig.usage | dvl::kGraphicsConfig.usage) == dvl::kSurfaceUsage);
    CHECK(dvl::kBackBufferHandleType == VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_KMT_BIT);
}

TEST_CASE("view_formats_unorm_only_with_storage")
{
    CHECK(dvl::view_formats(VK_FORMAT_R8G8B8A8_UNORM, kStorage) == Formats{VK_FORMAT_R8G8B8A8_UNORM});
    CHECK(dvl::view_formats(dvl::kComputeConfig.format, dvl::kComputeConfig.usage) ==
          Formats{VK_FORMAT_R8G8B8A8_UNORM});
    CHECK(dvl::view_formats(dvl::kGraphicsConfig.format, dvl::kGraphicsConfig.usage) ==
          Formats{VK_FORMAT_R8G8B8A8_UNORM, VK_FORMAT_R8G8B8A8_SRGB});
    CHECK(dvl::view_formats(VK_FORMAT_R8G8B8A8_UNORM, kColour | kSampledTransfer) ==
          Formats{VK_FORMAT_R8G8B8A8_UNORM, VK_FORMAT_R8G8B8A8_SRGB});
    CHECK(dvl::view_formats(VK_FORMAT_R8G8B8A8_UNORM, VK_IMAGE_USAGE_TRANSFER_DST_BIT) ==
          Formats{VK_FORMAT_R8G8B8A8_UNORM, VK_FORMAT_R8G8B8A8_SRGB});
    CHECK(dvl::view_formats(VK_FORMAT_R8G8B8A8_UNORM, kStorage | kColour) == Formats{VK_FORMAT_R8G8B8A8_UNORM});
}

TEST_CASE("view_formats_refuse_srgb_with_storage_and_other_formats")
{
    CHECK_FALSE(dvl::view_formats(VK_FORMAT_R8G8B8A8_SRGB, kStorage).has_value());
    CHECK_FALSE(dvl::view_formats(VK_FORMAT_R8G8B8A8_SRGB, kStorage | kSampledTransfer).has_value());
    CHECK_FALSE(dvl::view_formats(VK_FORMAT_B8G8R8A8_UNORM, kColour).has_value());
    CHECK_FALSE(dvl::view_formats(VK_FORMAT_A2B10G10R10_UNORM_PACK32, kStorage).has_value());
    CHECK_FALSE(dvl::view_formats(VK_FORMAT_UNDEFINED, kColour).has_value());
}

TEST_CASE("import_support_requires_importable")
{
    const dvl::ImportSupport ok =
        dvl::import_support(VK_SUCCESS, big_enough(), external(VK_EXTERNAL_MEMORY_FEATURE_IMPORTABLE_BIT),
                            kDreamAirExtent);
    CHECK(ok.ok);
    CHECK_FALSE(ok.dedicated_only);
    CHECK(ok.reason.empty());

    const dvl::ImportSupport export_only =
        dvl::import_support(VK_SUCCESS, big_enough(), external(VK_EXTERNAL_MEMORY_FEATURE_EXPORTABLE_BIT),
                            kDreamAirExtent);
    CHECK_FALSE(export_only.ok);
    CHECK(export_only.reason.find("importable") != std::string::npos);
    CHECK_FALSE(dvl::import_support(VK_SUCCESS, big_enough(), external(0), kDreamAirExtent).ok);

    const dvl::ImportSupport refused =
        dvl::import_support(VK_ERROR_FORMAT_NOT_SUPPORTED, big_enough(),
                            external(VK_EXTERNAL_MEMORY_FEATURE_IMPORTABLE_BIT), kDreamAirExtent);
    CHECK_FALSE(refused.ok);
    CHECK(refused.reason.find("-11") != std::string::npos);
    CHECK_FALSE(dvl::import_support(VK_ERROR_OUT_OF_HOST_MEMORY, big_enough(),
                                    external(VK_EXTERNAL_MEMORY_FEATURE_IMPORTABLE_BIT), kDreamAirExtent)
                    .ok);
}

TEST_CASE("import_support_notes_dedicated_only")
{
    const dvl::ImportSupport dedicated = dvl::import_support(
        VK_SUCCESS, big_enough(),
        external(VK_EXTERNAL_MEMORY_FEATURE_IMPORTABLE_BIT | VK_EXTERNAL_MEMORY_FEATURE_DEDICATED_ONLY_BIT),
        kDreamAirExtent);
    CHECK(dedicated.ok);
    CHECK(dedicated.dedicated_only);
}

TEST_CASE("import_support_needs_the_whole_extent")
{
    VkImageFormatProperties small = big_enough();
    small.maxExtent = {4096, 4096, 1};
    const dvl::ImportSupport too_small = dvl::import_support(
        VK_SUCCESS, small, external(VK_EXTERNAL_MEMORY_FEATURE_IMPORTABLE_BIT), kDreamAirExtent);
    CHECK_FALSE(too_small.ok);
    CHECK(too_small.reason.find("4096x4096") != std::string::npos);
    small.maxExtent = {7104, 3840, 1};
    CHECK(dvl::import_support(VK_SUCCESS, small, external(VK_EXTERNAL_MEMORY_FEATURE_IMPORTABLE_BIT),
                              kDreamAirExtent)
              .ok);
}

TEST_CASE("memory_type_from_handle_and_image_bits")
{
    const VkPhysicalDeviceMemoryProperties memory = memory_types({kHost, kLocal, kLocal | kHost, kLocal});

    dvl::MemoryPick pick = dvl::pick_memory_type(0b1100u, 0b0111u, memory);
    CHECK(pick.index == 2u);
    CHECK_FALSE(pick.fallback);
    pick = dvl::pick_memory_type(0b1111u, 0b1111u, memory);
    CHECK(pick.index == 1u);
    pick = dvl::pick_memory_type(0b0001u, 0b0011u, memory);
    CHECK(pick.index == 0u);
    CHECK_FALSE(pick.fallback);
    pick = dvl::pick_memory_type(0b110000u, 0b111111u, memory);
    CHECK_FALSE(pick.index.has_value());
}

TEST_CASE("memory_type_none_when_handle_and_image_disagree")
{
    const VkPhysicalDeviceMemoryProperties memory = memory_types({kHost, kLocal, kLocal});
    const dvl::MemoryPick pick = dvl::pick_memory_type(0b100u, 0b011u, memory);
    CHECK_FALSE(pick.index.has_value());
    CHECK_FALSE(pick.fallback);
    CHECK_FALSE(dvl::pick_memory_type(0b111u, 0u, memory).index.has_value());
}

TEST_CASE("memory_type_falls_back_to_image_bits")
{
    const VkPhysicalDeviceMemoryProperties memory = memory_types({kHost, kLocal, kLocal});
    for (const std::optional<uint32_t> handle : {std::optional<uint32_t>{}, std::optional<uint32_t>{0u}}) {
        INFO("handle bits " << (handle ? "0" : "none"));
        const dvl::MemoryPick pick = dvl::pick_memory_type(handle, 0b101u, memory);
        CHECK(pick.fallback);
        CHECK(pick.index == 2u);
        const dvl::MemoryPick host = dvl::pick_memory_type(handle, 0b001u, memory);
        CHECK(host.fallback);
        CHECK(host.index == 0u);
        const dvl::MemoryPick none = dvl::pick_memory_type(handle, 0u, memory);
        CHECK(none.fallback);
        CHECK_FALSE(none.index.has_value());
    }
}
