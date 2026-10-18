/**************************************************************************/
/*  managed_debugger_startup_barrier.cpp                                  */
/**************************************************************************/
/*                         This file is part of:                          */
/*                             GODOT ENGINE                               */
/*                        https://godotengine.org                          */
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

#include "managed_debugger_startup_barrier.h"

#include "core/os/os.h"

#ifdef UNIX_ENABLED
#include "core/io/stream_peer_uds.h"
#endif

namespace gdmono {

namespace {

constexpr char STARTUP_BARRIER_ENV[] = "XOGOT_MANAGED_DEBUG_SOCKET";
constexpr int STARTUP_BARRIER_MAX_COMMAND_BYTES = 32;

#ifdef UNIX_ENABLED
bool wait_for_connection(const Ref<StreamPeerUDS> &p_stream, String &r_error) {
	while (p_stream->get_status() == StreamPeerSocket::STATUS_CONNECTING) {
		Error err = p_stream->poll();
		if (err != OK) {
			r_error = vformat("the Unix socket connection failed (error %d)", err);
			return false;
		}
		OS::get_singleton()->delay_usec(1000);
	}

	if (p_stream->get_status() != StreamPeerSocket::STATUS_CONNECTED) {
		r_error = "the editor refused the Unix socket connection";
		return false;
	}
	return true;
}

bool read_command(const Ref<StreamPeerUDS> &p_stream, String &r_command, String &r_error) {
	for (int i = 0; i < STARTUP_BARRIER_MAX_COMMAND_BYTES; i++) {
		// There is intentionally no elapsed-time deadline here. A live editor may
		// take an arbitrary amount of time to attach and configure breakpoints;
		// ABORT or socket EOF are the deterministic cancellation signals.
		Error err = p_stream->wait(NetSocket::POLL_TYPE_IN, -1);
		if (err != OK) {
			r_error = vformat("failed while waiting for the debugger response (error %d)", err);
			return false;
		}

		uint8_t byte = 0;
		int received = 0;
		err = p_stream->get_partial_data(&byte, 1, received);
		if (err == ERR_FILE_EOF || (err == OK && received == 0)) {
			r_error = "the editor closed the startup barrier before releasing the game";
			return false;
		}
		if (err != OK) {
			r_error = vformat("failed to read the debugger response (error %d)", err);
			return false;
		}
		if (byte == '\n') {
			return true;
		}
		if (byte < 0x20 || byte > 0x7e) {
			r_error = "the editor sent a malformed startup barrier command";
			return false;
		}
		r_command += String::chr(byte);
	}

	r_error = "the editor sent an oversized startup barrier command";
	return false;
}
#endif

} // namespace

bool wait_for_managed_debugger_startup(String &r_error) {
	OS *os = OS::get_singleton();
	String socket_path = os->get_environment(STARTUP_BARRIER_ENV);
	if (socket_path.is_empty()) {
		return true;
	}

	// The barrier is one-shot. Do not let user-created descendants inherit it.
	os->unset_environment(STARTUP_BARRIER_ENV);

#ifndef UNIX_ENABLED
	r_error = "Unix-domain sockets are unavailable on this platform";
	return false;
#else
	Ref<StreamPeerUDS> stream;
	stream.instantiate();
	Error err = stream->connect_to_host(socket_path);
	if (err != OK) {
		r_error = vformat("could not connect to editor socket '%s' (error %d)", socket_path, err);
		return false;
	}

	if (!wait_for_connection(stream, r_error)) {
		return false;
	}

	String ready = vformat("XOGOT-MANAGED-DEBUG 1 READY %d\n", os->get_process_id());
	CharString ready_utf8 = ready.utf8();
	err = stream->put_data((const uint8_t *)ready_utf8.get_data(), ready_utf8.length());
	if (err != OK) {
		r_error = vformat("could not notify the editor that CoreCLR is ready (error %d)", err);
		return false;
	}

	String command;
	if (!read_command(stream, command, r_error)) {
		return false;
	}
	if (command == "ABORT") {
		r_error = "the editor aborted managed debugger startup";
		return false;
	}
	if (command != "CONTINUE") {
		r_error = vformat("the editor sent an unknown startup barrier command '%s'", command);
		return false;
	}

	stream->disconnect_from_host();
	return true;
#endif
}

} // namespace gdmono
