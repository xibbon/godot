/**************************************************************************/
/*  test_animation_player.h                                               */
/**************************************************************************/
/*                         This file is part of:                          */
/*                             GODOT ENGINE                               */
/*                        https://godotengine.org                         */
/**************************************************************************/
/* Copyright (c) 2014-present Godot Engine contributors (see AUTHORS.md). */
/* Copyright (c) 2007-2014 Juan Linietsky, Ariel Manzur.                  */
/*                                                                        */
/* Permission is hereby granted, free of charge, to any person obtaining  */
/* a copy of this software and associated documentation files (the        */
/* "Software"), to deal in the Software without restriction, including    */
/* without limitation the rights to use, copy, modify, merge, publish,    */
/* distribute, sublicense, and/or sell copies of the Software, and to     */
/* permit persons to whom the Software is furnished to do so, subject to  */
/* the following conditions:                                              */
/*                                                                        */
/* The above copyright notice and this permission notice shall be         */
/* included in all copies or substantial portions of the Software.        */
/*                                                                        */
/* THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,        */
/* EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF     */
/* MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. */
/* IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY   */
/* CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,   */
/* TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE      */
/* SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.                 */
/**************************************************************************/

#pragma once

#include "scene/2d/node_2d.h"
#include "scene/animation/animation_player.h"
#include "scene/main/scene_tree.h"
#include "scene/main/window.h"
#include "scene/resources/animation.h"
#include "tests/test_macros.h"

namespace TestAnimationPlayer {

TEST_CASE("[AnimationPlayer] get & set default_blend_time") {
	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_default_blend_time(4.0);

	CHECK(animation_player->get_default_blend_time() == doctest::Approx(4.0f));
	memdelete(animation_player);
}

TEST_CASE("[AnimationPlayer] get & set blend_time") {
	String anim1 = "animation1";
	String anim2 = "animation2";
	const Ref<Animation> animation1 = memnew(Animation);
	const Ref<Animation> animation2 = memnew(Animation);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation(anim1, animation1);
	animation_library->add_animation(anim2, animation2);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);

	animation_player->set_blend_time(anim1, anim2, 4.0);
	CHECK(animation_player->get_blend_time(anim1, anim2) == doctest::Approx(4.0f));
	memdelete(animation_player);
}

TEST_CASE("[AnimationPlayer] removing animations keeps playback state valid") {
	const Ref<Animation> animation1 = memnew(Animation);
	const Ref<Animation> animation2 = memnew(Animation);
	animation1->set_length(1.0);
	animation2->set_length(1.0);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation1", animation1);
	animation_library->add_animation("animation2", animation2);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	animation_player->set_blend_time("animation1", "animation2", 0.5);
	animation_player->play("animation2");
	animation_player->advance(0.0);

	animation_library->remove_animation("animation1");
	CHECK(animation_player->is_valid());
	CHECK(animation_player->get_assigned_animation() == StringName("animation2"));
	CHECK(animation_player->get_current_animation_length() == doctest::Approx(1.0));
	CHECK(animation_player->get_blend_time("animation1", "animation2") == doctest::Approx(0.5));
	// A blend time whose animation is currently missing stays serialized. Hiding
	// it would permanently delete it from the scene on any save taken while the
	// animation is absent -- during a reimport, while an external library fails
	// to resolve, or between the halves of an undo step.
	Array serialized_blends = animation_player->get(SceneStringName(blend_times));
	CHECK(serialized_blends.size() == 3);
	animation_player->advance(0.25);

	animation_library->remove_animation("animation2");
	CHECK_FALSE(animation_player->is_valid());
	animation_player->advance(0.0);
	CHECK_FALSE(animation_player->is_playing());
	CHECK(animation_player->get_assigned_animation().is_empty());

	animation_library->add_animation("animation1", animation1);
	animation_library->add_animation("animation2", animation2);
	CHECK(animation_player->get_blend_time("animation1", "animation2") == doctest::Approx(0.5));
	serialized_blends = animation_player->get(SceneStringName(blend_times));
	CHECK(serialized_blends.size() == 3);
	animation_player->play("animation2");
	animation_player->advance(0.25);
	CHECK(animation_player->is_valid());
	memdelete(animation_player);
}

TEST_CASE("[AnimationPlayer] removing the current animation starts a valid queued animation") {
	const Ref<Animation> animation1 = memnew(Animation);
	const Ref<Animation> animation2 = memnew(Animation);
	animation1->set_length(1.0);
	animation2->set_length(1.0);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation1", animation1);
	animation_library->add_animation("animation2", animation2);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	animation_player->play("animation1");
	animation_player->queue("animation2");
	animation_library->remove_animation("animation1");

	Callable play_when_caches_clear = callable_mp(animation_player, &AnimationPlayer::play).bind(StringName("animation2"), -1.0, 1.0, false);
	animation_player->connect(SNAME("caches_cleared"), play_when_caches_clear, Object::CONNECT_ONE_SHOT);
	animation_player->advance(0.0);
	CHECK(animation_player->is_playing());
	CHECK(animation_player->is_valid());
	CHECK(animation_player->get_assigned_animation() == StringName("animation2"));
	memdelete(animation_player);
}

TEST_CASE("[AnimationPlayer] deleting an assigned animation after playback stops hides the stale name") {
	const Ref<Animation> animation = memnew(Animation);
	animation->set_length(0.5);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation", animation);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	animation_player->play("animation");
	animation_player->advance(0.5);
	CHECK_FALSE(animation_player->is_playing());

	animation_library->remove_animation("animation");
	CHECK_FALSE(animation_player->is_valid());
	CHECK(animation_player->get_assigned_animation().is_empty());
	animation_player->seek(0.0);
	memdelete(animation_player);
}

TEST_CASE("[SceneTree][AnimationPlayer] stop without keeping state applies the start pose") {
	const Ref<Animation> animation = memnew(Animation);
	animation->set_length(1.0);
	const int value_track = animation->add_track(Animation::TYPE_VALUE);
	animation->track_set_path(value_track, NodePath("target:position"));
	animation->track_insert_key(value_track, 0.0, Vector2());
	animation->track_insert_key(value_track, 1.0, Vector2(10.0, 0.0));
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation", animation);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	Node2D *target = memnew(Node2D);
	target->set_name("target");
	animation_player->add_child(target);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	SceneTree::get_singleton()->get_root()->add_child(animation_player);

	animation_player->play("animation");
	animation_player->advance(0.5);
	CHECK(target->get_position().is_equal_approx(Vector2(5.0, 0.0)));
	animation_player->stop(true);
	CHECK(target->get_position().is_equal_approx(Vector2(5.0, 0.0)));
	animation_player->stop(false);
	CHECK(target->get_position().is_zero_approx());
	memdelete(animation_player);
}

TEST_CASE("[AnimationPlayer] replacing the current animation keeps playback and blend times") {
	const Ref<Animation> original_animation = memnew(Animation);
	const Ref<Animation> replacement_animation = memnew(Animation);
	original_animation->set_length(1.0);
	replacement_animation->set_length(2.0);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("current", original_animation);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	animation_player->set_blend_time("current", "current", 0.5);
	animation_player->play("current");
	animation_player->advance(0.25);

	animation_library->add_animation("current", replacement_animation);
	CHECK(animation_player->is_playing());
	CHECK(animation_player->is_valid());
	CHECK(animation_player->get_assigned_animation() == StringName("current"));
	CHECK(animation_player->get_current_animation_position() == doctest::Approx(0.25));
	CHECK(animation_player->get_current_animation_length() == doctest::Approx(2.0));
	CHECK(animation_player->get_blend_time("current", "current") == doctest::Approx(0.5));
	animation_player->advance(0.25);
	CHECK(animation_player->get_current_animation_position() == doctest::Approx(0.5));
	memdelete(animation_player);
}

TEST_CASE("[AnimationPlayer] renaming the assigned animation keeps it valid") {
	const Ref<Animation> animation = memnew(Animation);
	animation->set_length(1.0);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("before", animation);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	animation_player->play("before");
	animation_player->advance(0.0);

	Callable seek_when_caches_clear = callable_mp(animation_player, &AnimationPlayer::seek).bind(0.5, false, false);
	animation_player->connect(SNAME("caches_cleared"), seek_when_caches_clear, Object::CONNECT_ONE_SHOT);
	animation_library->rename_animation("before", "after");
	CHECK(animation_player->is_valid());
	CHECK(animation_player->get_assigned_animation() == StringName("after"));
	CHECK(animation_player->get_current_animation_length() == doctest::Approx(1.0));
	CHECK(animation_player->get_current_animation_position() == doctest::Approx(0.5));
	animation_player->advance(0.25);

	Callable stop_when_caches_clear = callable_mp(animation_player, &AnimationPlayer::stop).bind(true);
	animation_player->connect(SNAME("caches_cleared"), stop_when_caches_clear, Object::CONNECT_ONE_SHOT);
	animation_library->rename_animation("after", "final");
	CHECK_FALSE(animation_player->is_valid());
	CHECK(animation_player->get_assigned_animation() == StringName("final"));
	memdelete(animation_player);
}

TEST_CASE("[AnimationPlayer] renaming a library validates before changing playback state") {
	const Ref<Animation> source_animation = memnew(Animation);
	const Ref<Animation> target_animation = memnew(Animation);
	source_animation->set_length(1.0);
	target_animation->set_length(1.0);
	const Ref<AnimationLibrary> source_library = memnew(AnimationLibrary);
	const Ref<AnimationLibrary> target_library = memnew(AnimationLibrary);
	source_library->add_animation("clip", source_animation);
	target_library->add_animation("clip", target_animation);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("a", source_library);
	animation_player->add_animation_library("z", target_library);
	animation_player->set_autoplay("a/clip");
	animation_player->set_blend_time("a/clip", "z/clip", 0.5);
	animation_player->animation_set_next("a/clip", "z/clip");
	animation_player->animation_set_next("z/clip", "a/clip");
	animation_player->play("a/clip");
	animation_player->advance(0.25);

	ERR_PRINT_OFF;
	animation_player->rename_animation_library("a", "z");
	ERR_PRINT_ON;
	CHECK(animation_player->has_animation_library("a"));
	CHECK(animation_player->has_animation_library("z"));
	CHECK(animation_player->get_assigned_animation() == StringName("a/clip"));
	CHECK(animation_player->get_autoplay() == StringName("a/clip"));
	CHECK(animation_player->get_blend_time("a/clip", "z/clip") == doctest::Approx(0.5));
	CHECK(animation_player->animation_get_next("a/clip") == StringName("z/clip"));
	CHECK(animation_player->animation_get_next("z/clip") == StringName("a/clip"));
	CHECK(animation_player->is_valid());

	Callable seek_when_library_caches_clear = callable_mp(animation_player, &AnimationPlayer::seek).bind(0.5, false, false);
	animation_player->connect(SNAME("caches_cleared"), seek_when_library_caches_clear, Object::CONNECT_ONE_SHOT);
	animation_player->rename_animation_library("a", "b");
	CHECK_FALSE(animation_player->has_animation_library("a"));
	CHECK(animation_player->has_animation_library("b"));
	CHECK(animation_player->get_assigned_animation() == StringName("b/clip"));
	CHECK(animation_player->get_autoplay() == StringName("b/clip"));
	CHECK(animation_player->get_blend_time("b/clip", "z/clip") == doctest::Approx(0.5));
	CHECK(animation_player->animation_get_next("b/clip") == StringName("z/clip"));
	CHECK(animation_player->animation_get_next("z/clip") == StringName("b/clip"));
	CHECK(animation_player->is_valid());
	CHECK(animation_player->get_current_animation_position() == doctest::Approx(0.5));
	animation_player->advance(0.25);
	memdelete(animation_player);
}

TEST_CASE("[SceneTree][AnimationPlayer] an invalid queued animation is skipped on completion") {
	const Ref<Animation> animation1 = memnew(Animation);
	const Ref<Animation> animation2 = memnew(Animation);
	animation1->set_length(0.5);
	animation2->set_length(0.5);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation1", animation1);
	animation_library->add_animation("animation2", animation2);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	SceneTree::get_singleton()->get_root()->add_child(animation_player);
	SIGNAL_WATCH(animation_player, SceneStringName(animation_finished));
	SIGNAL_WATCH(animation_player, SNAME("current_animation_changed"));
	SIGNAL_WATCH(animation_player, SceneStringName(animation_changed));

	animation_player->play("animation1");
	SIGNAL_DISCARD(SNAME("current_animation_changed"));
	animation_player->queue("animation2");
	animation_library->remove_animation("animation2");
	animation_player->advance(0.5);

	CHECK_FALSE(animation_player->is_playing());
	Array finished_args = { { StringName("animation1") } };
	SIGNAL_CHECK(SceneStringName(animation_finished), finished_args);
	Array current_args = { { "" } };
	SIGNAL_CHECK(SNAME("current_animation_changed"), current_args);
	SIGNAL_CHECK_FALSE(SceneStringName(animation_changed));
	SIGNAL_UNWATCH(animation_player, SceneStringName(animation_finished));
	SIGNAL_UNWATCH(animation_player, SNAME("current_animation_changed"));
	SIGNAL_UNWATCH(animation_player, SceneStringName(animation_changed));
	memdelete(animation_player);
}

TEST_CASE("[SceneTree][AnimationPlayer] removing an animation on an ending seek reports the current animation change") {
	const Ref<Animation> animation = memnew(Animation);
	animation->set_length(0.5);
	const int method_track = animation->add_track(Animation::TYPE_METHOD);
	animation->track_set_path(method_track, NodePath("."));
	Dictionary method_key;
	method_key["method"] = "remove_animation_library";
	Array method_args;
	method_args.push_back(StringName());
	method_key["args"] = method_args;
	animation->track_insert_key(method_track, 0.5, method_key);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation", animation);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->set_callback_mode_method(AnimationMixer::ANIMATION_CALLBACK_MODE_METHOD_IMMEDIATE);
	animation_player->add_animation_library("", animation_library);
	SceneTree::get_singleton()->get_root()->add_child(animation_player);
	SIGNAL_WATCH(animation_player, SNAME("current_animation_changed"));

	animation_player->play("animation");
	SIGNAL_DISCARD(SNAME("current_animation_changed"));
	animation_player->seek(0.5, true);
	CHECK_FALSE(animation_player->is_valid());
	Array current_args = { { "" } };
	SIGNAL_CHECK(SNAME("current_animation_changed"), current_args);
	SIGNAL_UNWATCH(animation_player, SNAME("current_animation_changed"));
	memdelete(animation_player);
}

TEST_CASE("[AnimationPlayer] removing a library preserves blend times") {
	const Ref<Animation> animation1 = memnew(Animation);
	const Ref<Animation> animation2 = memnew(Animation);
	animation1->set_length(1.0);
	animation2->set_length(1.0);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation1", animation1);
	animation_library->add_animation("animation2", animation2);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("library", animation_library);
	animation_player->set_blend_time("library/animation1", "library/animation2", 0.5);
	animation_player->play("library/animation1");
	animation_player->advance(0.0);
	animation_player->play("library/animation2", 0.5);
	animation_player->advance(0.25);

	animation_player->remove_animation_library("library");
	CHECK_FALSE(animation_player->is_valid());
	CHECK(animation_player->get_blend_time("library/animation1", "library/animation2") == doctest::Approx(0.5));

	animation_player->add_animation_library("library", animation_library);
	animation_player->play("library/animation2");
	animation_player->advance(0.25);
	CHECK(animation_player->is_valid());
	memdelete(animation_player);
}

TEST_CASE("[SceneTree][AnimationPlayer] queued playback keeps signal handler queue changes") {
	const Ref<Animation> animation1 = memnew(Animation);
	const Ref<Animation> animation2 = memnew(Animation);
	const Ref<Animation> animation3 = memnew(Animation);
	animation1->set_length(0.5);
	animation2->set_length(0.5);
	animation3->set_length(0.5);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation1", animation1);
	animation_library->add_animation("animation2", animation2);
	animation_library->add_animation("animation3", animation3);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	SceneTree::get_singleton()->get_root()->add_child(animation_player);
	SIGNAL_WATCH(animation_player, SceneStringName(animation_changed));

	animation_player->play("animation1");
	animation_player->queue("animation2");
	animation_player->queue("animation3");
	Callable clear_queue_when_started = callable_mp(animation_player, &AnimationPlayer::clear_queue).unbind(1);
	animation_player->connect(SceneStringName(animation_started), clear_queue_when_started, Object::CONNECT_ONE_SHOT);
	animation_player->advance(0.5);

	CHECK(animation_player->is_playing());
	CHECK(animation_player->get_assigned_animation() == StringName("animation2"));
	CHECK(animation_player->get_queue().is_empty());
	Array changed_args = { { String("animation1"), StringName("animation2") } };
	SIGNAL_CHECK(SceneStringName(animation_changed), changed_args);
	SIGNAL_UNWATCH(animation_player, SceneStringName(animation_changed));
	memdelete(animation_player);
}

TEST_CASE("[SceneTree][AnimationPlayer] an explicit play from a queued transition handler clears the queue") {
	const Ref<Animation> animation1 = memnew(Animation);
	const Ref<Animation> animation2 = memnew(Animation);
	const Ref<Animation> animation3 = memnew(Animation);
	const Ref<Animation> hurt_animation = memnew(Animation);
	animation1->set_length(0.5);
	animation2->set_length(0.5);
	animation3->set_length(0.5);
	hurt_animation->set_length(0.5);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation1", animation1);
	animation_library->add_animation("animation2", animation2);
	animation_library->add_animation("animation3", animation3);
	animation_library->add_animation("hurt", hurt_animation);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	SceneTree::get_singleton()->get_root()->add_child(animation_player);

	animation_player->play("animation1");
	animation_player->queue("animation2");
	animation_player->queue("animation3");
	Callable play_hurt_when_started = callable_mp(animation_player, &AnimationPlayer::play).bind(StringName("hurt"), -1.0, 1.0, false).unbind(1);
	animation_player->connect(SceneStringName(animation_started), play_hurt_when_started, Object::CONNECT_ONE_SHOT);
	animation_player->advance(0.5);

	CHECK(animation_player->is_playing());
	CHECK(animation_player->get_assigned_animation() == StringName("hurt"));
	CHECK(animation_player->get_queue().is_empty());
	memdelete(animation_player);
}

TEST_CASE("[SceneTree][AnimationPlayer] queued playback reports a transition when a signal handler stops it") {
	const Ref<Animation> animation1 = memnew(Animation);
	const Ref<Animation> animation2 = memnew(Animation);
	animation1->set_length(0.5);
	animation2->set_length(0.5);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation1", animation1);
	animation_library->add_animation("animation2", animation2);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	SceneTree::get_singleton()->get_root()->add_child(animation_player);
	SIGNAL_WATCH(animation_player, SceneStringName(animation_changed));

	animation_player->play("animation1");
	animation_player->queue("animation2");
	Callable stop_when_started = callable_mp(animation_player, &AnimationPlayer::stop).bind(true).unbind(1);
	animation_player->connect(SceneStringName(animation_started), stop_when_started, Object::CONNECT_ONE_SHOT);
	animation_player->advance(0.5);

	CHECK_FALSE(animation_player->is_playing());
	Array changed_args = { { String("animation1"), StringName("animation2") } };
	SIGNAL_CHECK(SceneStringName(animation_changed), changed_args);
	SIGNAL_UNWATCH(animation_player, SceneStringName(animation_changed));
	memdelete(animation_player);
}

TEST_CASE("[SceneTree][AnimationPlayer] clearing the queue from animation finished stops in the same frame") {
	const Ref<Animation> animation1 = memnew(Animation);
	const Ref<Animation> animation2 = memnew(Animation);
	animation1->set_length(0.5);
	animation2->set_length(0.5);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation1", animation1);
	animation_library->add_animation("animation2", animation2);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	SceneTree::get_singleton()->get_root()->add_child(animation_player);
	SIGNAL_WATCH(animation_player, SceneStringName(animation_finished));
	SIGNAL_WATCH(animation_player, SNAME("current_animation_changed"));
	SIGNAL_WATCH(animation_player, SceneStringName(animation_changed));

	animation_player->play("animation1");
	SIGNAL_DISCARD(SNAME("current_animation_changed"));
	animation_player->queue("animation2");
	Callable clear_queue_when_finished = callable_mp(animation_player, &AnimationPlayer::clear_queue).unbind(1);
	animation_player->connect(SceneStringName(animation_finished), clear_queue_when_finished, Object::CONNECT_ONE_SHOT);
	animation_player->advance(0.5);

	CHECK_FALSE(animation_player->is_playing());
	CHECK(animation_player->get_queue().is_empty());
	Array finished_args = { { StringName("animation1") } };
	SIGNAL_CHECK(SceneStringName(animation_finished), finished_args);
	Array current_args = { { "" } };
	SIGNAL_CHECK(SNAME("current_animation_changed"), current_args);
	SIGNAL_CHECK_FALSE(SceneStringName(animation_changed));
	SIGNAL_UNWATCH(animation_player, SceneStringName(animation_finished));
	SIGNAL_UNWATCH(animation_player, SNAME("current_animation_changed"));
	SIGNAL_UNWATCH(animation_player, SceneStringName(animation_changed));
	memdelete(animation_player);
}

TEST_CASE("[SceneTree][AnimationPlayer] restarting playback from caches cleared keeps animation finished") {
	const Ref<Animation> animation1 = memnew(Animation);
	const Ref<Animation> animation2 = memnew(Animation);
	animation1->set_length(0.5);
	animation2->set_length(0.5);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation1", animation1);
	animation_library->add_animation("animation2", animation2);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	SceneTree::get_singleton()->get_root()->add_child(animation_player);
	SIGNAL_WATCH(animation_player, SceneStringName(animation_finished));

	animation_player->play("animation1");
	Callable restart_when_caches_clear = callable_mp(animation_player, &AnimationPlayer::play).bind(StringName("animation2"), -1.0, 1.0, false);
	animation_player->connect(SNAME("caches_cleared"), restart_when_caches_clear, Object::CONNECT_ONE_SHOT);
	animation_player->advance(0.5);

	CHECK(animation_player->is_playing());
	CHECK(animation_player->get_assigned_animation() == StringName("animation2"));
	Array finished_args = { { StringName("animation1") } };
	SIGNAL_CHECK(SceneStringName(animation_finished), finished_args);
	SIGNAL_UNWATCH(animation_player, SceneStringName(animation_finished));
	memdelete(animation_player);
}

TEST_CASE("[AnimationPlayer] pause does not restore an animation cleared by stop") {
	const Ref<Animation> animation = memnew(Animation);
	animation->set_length(0.5);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation", animation);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	animation_player->play("animation");
	animation_player->stop(false);
	CHECK_FALSE(animation_player->is_valid());

	animation_player->pause();
	CHECK_FALSE(animation_player->is_valid());
	memdelete(animation_player);
}

TEST_CASE("[AnimationPlayer] restoring a removed animation restores its assigned name") {
	const Ref<Animation> animation = memnew(Animation);
	animation->set_length(0.5);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation", animation);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	animation_player->play("animation");
	animation_library->remove_animation("animation");
	animation_player->advance(0.0);
	CHECK(animation_player->get_assigned_animation().is_empty());

	animation_library->add_animation("animation", animation);
	CHECK(animation_player->get_assigned_animation() == StringName("animation"));
	CHECK_FALSE(animation_player->is_valid());
	animation_player->play();
	CHECK(animation_player->is_playing());
	CHECK(animation_player->is_valid());
	memdelete(animation_player);
}

TEST_CASE("[SceneTree][AnimationPlayer] replaying the same animation from animation finished is not cancelled") {
	const Ref<Animation> animation1 = memnew(Animation);
	const Ref<Animation> animation2 = memnew(Animation);
	animation1->set_length(0.5);
	animation2->set_length(0.5);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation1", animation1);
	animation_library->add_animation("animation2", animation2);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	SceneTree::get_singleton()->get_root()->add_child(animation_player);

	animation_player->play("animation1");
	animation_player->queue("animation2");
	// The handler restarts the animation that just finished. The name is
	// therefore unchanged, so only a restart counter can tell this apart from
	// the handler doing nothing -- comparing names cancelled the replay.
	Callable replay_when_finished = callable_mp(animation_player, &AnimationPlayer::play).bind(StringName("animation1"), -1.0, 1.0, false).unbind(1);
	animation_player->connect(SceneStringName(animation_finished), replay_when_finished, Object::CONNECT_ONE_SHOT);
	animation_player->advance(0.5);

	CHECK(animation_player->is_playing());
	CHECK(animation_player->get_assigned_animation() == StringName("animation1"));
	memdelete(animation_player);
}

TEST_CASE("[SceneTree][AnimationPlayer] pausing a recovered queued animation keeps it assigned") {
	const Ref<Animation> animation1 = memnew(Animation);
	const Ref<Animation> animation2 = memnew(Animation);
	animation1->set_length(0.5);
	animation2->set_length(0.5);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation1", animation1);
	animation_library->add_animation("animation2", animation2);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	SceneTree::get_singleton()->get_root()->add_child(animation_player);
	SIGNAL_WATCH(animation_player, SceneStringName(animation_changed));

	animation_player->play("animation1");
	animation_player->queue("animation2");
	animation_player->advance(0.0);
	// A handler pauses the animation the recovery just started. That leaves a
	// valid assigned animation, which must not be treated as "recovery failed".
	Callable pause_when_started = callable_mp(animation_player, &AnimationPlayer::pause).unbind(1);
	animation_player->connect(SceneStringName(animation_started), pause_when_started, Object::CONNECT_ONE_SHOT);
	animation_library->remove_animation("animation1");
	animation_player->advance(0.0);

	CHECK(animation_player->get_assigned_animation() == StringName("animation2"));
	// The previous animation was removed rather than finished, so this is not a
	// transition that animation_changed should report.
	SIGNAL_CHECK_FALSE(SceneStringName(animation_changed));
	SIGNAL_UNWATCH(animation_player, SceneStringName(animation_changed));
	memdelete(animation_player);
}

TEST_CASE("[AnimationPlayer] renaming onto an existing next animation key is deterministic") {
	const Ref<Animation> walk = memnew(Animation);
	const Ref<Animation> run = memnew(Animation);
	const Ref<Animation> idle = memnew(Animation);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("walk", walk);
	animation_library->add_animation("run", run);
	animation_library->add_animation("idle", idle);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	// "run" both collides with the rename target and points at the renamed
	// animation, so it produces the same destination key as "walk" itself.
	animation_player->animation_set_next("walk", "idle");
	animation_player->animation_set_next("run", "walk");

	// Free the target name. Nothing prunes animation_next_set on removal, so the
	// stale "run" entry survives and is still there to collide with the rename.
	animation_library->remove_animation("run");
	animation_library->rename_animation("walk", "run");

	// The renamed animation's own next animation wins, whatever the hash order.
	CHECK(animation_player->animation_get_next("run") == StringName("idle"));
	memdelete(animation_player);
}

TEST_CASE("[SceneTree][AnimationPlayer] renaming an animation follows it through the playback queue") {
	const Ref<Animation> animation1 = memnew(Animation);
	const Ref<Animation> walk = memnew(Animation);
	animation1->set_length(0.5);
	walk->set_length(0.5);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation1", animation1);
	animation_library->add_animation("walk", walk);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);
	SceneTree::get_singleton()->get_root()->add_child(animation_player);

	animation_player->play("animation1");
	animation_player->queue("walk");
	// Every other name-keyed structure follows a rename; a queue entry left
	// behind would be silently popped as unresolvable when animation1 finishes.
	animation_library->rename_animation("walk", "run");
	animation_player->advance(0.5);

	CHECK(animation_player->is_playing());
	CHECK(animation_player->get_assigned_animation() == StringName("run"));
	memdelete(animation_player);
}

TEST_CASE("[AnimationPlayer] a negative serialized blend time is rejected") {
	const Ref<Animation> animation1 = memnew(Animation);
	const Ref<Animation> animation2 = memnew(Animation);
	const Ref<AnimationLibrary> animation_library = memnew(AnimationLibrary);
	animation_library->add_animation("animation1", animation1);
	animation_library->add_animation("animation2", animation2);

	AnimationPlayer *animation_player = memnew(AnimationPlayer);
	animation_player->set_root_node(NodePath("."));
	animation_player->add_animation_library("", animation_library);

	// Deserialization tolerates names that are not present, but not an invalid
	// time. The entry is skipped rather than silently stored.
	Array blends = { StringName("animation1"), StringName("animation2"), -1.0 };
	ERR_PRINT_OFF;
	animation_player->set(SceneStringName(blend_times), blends);
	ERR_PRINT_ON;
	CHECK(animation_player->get_blend_time("animation1", "animation2") == doctest::Approx(0.0));

	// A blend time for an animation that is not currently present is kept.
	Array absent = { StringName("animation1"), StringName("missing"), 0.25 };
	animation_player->set(SceneStringName(blend_times), absent);
	CHECK(animation_player->get_blend_time("animation1", "missing") == doctest::Approx(0.25));
	memdelete(animation_player);
}

} // namespace TestAnimationPlayer
