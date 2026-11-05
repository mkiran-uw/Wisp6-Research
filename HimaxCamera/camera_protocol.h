#ifndef CAMERA_PROTOCOL_H_
#define CAMERA_PROTOCOL_H_

#include <stdint.h>
#include "cam.h"

typedef enum {
    CAMERA_IDLE = 0,
    CAMERA_BUSY = 1,
    CAMERA_READY = 2,
    CAMERA_ERROR = 3
} camera_state_t;

typedef enum {
    CAMERA_OK = 0,
    CAMERA_I2C_NACK = 1,
    CAMERA_I2C_TIMEOUT = 2,
    CAMERA_I2C_ARBITRATION = 3,
    CAMERA_WRONG_ID = 4,
    CAMERA_FRAME_TIMEOUT = 5,
    CAMERA_BAD_COMMAND = 6,
    CAMERA_NO_FRAME = 7,
    CAMERA_BAD_BANK = 8
} camera_error_t;

typedef struct {
    camera_state_t state;
    camera_error_t error;
    uint16_t frame_id;
    uint16_t checksum;
    uint16_t capture_command;
    uint16_t selected_chunk;
    uint8_t has_request;
    uint8_t has_frame;
} camera_session_t;

void camera_session_init(camera_session_t *session);
uint8_t camera_command(camera_session_t *session, uint16_t command);
void camera_complete(camera_session_t *session, camera_error_t error, const uint8_t *image);
void camera_window(const camera_session_t *session, const uint8_t *image, uint8_t *window);
uint16_t camera_crc16(const uint8_t *data, uint16_t size);

#endif
