(*
 * Copyright 2015 INRIA and Microsoft Corporation
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *)

#light "off"

module FlexTLS.FlexCCS

open NLog

open Bytes
open Error
open TLSConstants

open FlexTypes
open FlexConstants
open FlexState
open FlexRecord

/// <summary>
/// Module receiving, sending and forwarding TLS ChangeCipherSpec messages.
/// </summary>
type FlexCCS =
    class

    /// <summary>
    /// Receive CCS message from network stream
    /// </summary>
    /// <param name="st"> State of the current Handshake </param>
    /// <returns> Updated state * CCS record * CCS byte </returns>
    static member receive (st:state) : state * FChangeCipherSpecs * bytes =
        LogManager.GetLogger("file").Info("# CCS : FlexCCS.receive");
        let ct,pv,len,_ = FlexRecord.parseFragmentHeader st in
        match ct with
        | Change_cipher_spec ->
            let st,payload = FlexRecord.getFragmentContent(st,Change_cipher_spec,len) in
            if payload = HandshakeMessages.CCSBytes then
                (LogManager.GetLogger("file").Debug(sprintf "--- Payload : %s" (Bytes.hexString(payload)));
                st,{payload = payload },payload)
            else
                failwith (perror __SOURCE_FILE__ __LINE__ "Unexpected CCS content")
        | _ ->
            let _,b = FlexRecord.getFragmentContent (st, ct, len) in
            failwith (perror __SOURCE_FILE__ __LINE__ (sprintf "Unexpected content type : %A\n Payload (%d Bytes) : %s" ct len (Bytes.hexString(b))))

    /// <summary>
    /// Forward CCS to the network stream
    /// </summary>
    /// <param name="stin"> State of the current Handshake on the incoming side </param>
    /// <param name="stout"> State of the current Handshake on the outgoing side </param>
    /// <returns> Updated incoming state * Updated outgoing state * forwarded CCS byte </returns>
    static member forward (stin:state, stout:state) : state * state * bytes =
        LogManager.GetLogger("file").Info("# CCS : FlexCCS.forward");
        let stin,ccs,msgb  = FlexCCS.receive(stin) in
        let stout,_ = FlexCCS.send(stout) in
        LogManager.GetLogger("file").Debug(sprintf "--- Payload : %s" (Bytes.hexString(msgb)));
        stin,stout,msgb

    /// <summary>
    /// Send CCS to the network stream
    /// </summary>
    /// <param name="st"> State of the current Handshake on the incoming side </param>
    /// <param name="fccs"> Optional CCS message record </param>
    /// <returns> Updated state * CCS message record </returns>
    static member send (st:state, ?fccs:FChangeCipherSpecs) : state * FChangeCipherSpecs =
        LogManager.GetLogger("file").Info("# CCS : FlexCCS.send");
        let fccs = defaultArg fccs FlexConstants.nullFChangeCipherSpecs in
        let record_write,_,_ = FlexRecord.send(
                st.ns, st.write.epoch, st.write.record,
                Change_cipher_spec, fccs.payload,
                st.write.epoch_init_pv) in
        let st = FlexState.updateOutgoingRecord st record_write in
        LogManager.GetLogger("file").Debug(sprintf "--- Payload : %s" (Bytes.hexString(fccs.payload)));
        st,fccs

    end
